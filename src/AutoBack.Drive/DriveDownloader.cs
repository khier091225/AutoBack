using System.Security.Cryptography;
using AutoBack.Backup;
using AutoBack.Connection;

namespace AutoBack.Drive;

public sealed record DownloadProgress(long Bytes, long TotalBytes, string Detail);
public sealed record DownloadedBackup(string Path, string FileId, Guid BackupJobId, Guid BackupAgentId, string Sha256, long Bytes);

public sealed class DriveDownloader(ProtectedStore store, HttpClient http)
{
    public static string ParseFileId(string input)
    {
        if (CloudStates.ValidId(input)) return input;
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "drive.google.com" &&
            uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length is 3 or 4 && segments[0] == "file" && segments[1] == "d" && CloudStates.ValidId(segments[2]) &&
                (segments.Length == 3 || segments[3] == "view")) return segments[2];
        }
        throw new ArgumentException("Provide a Google Drive file ID or its https://drive.google.com/file/d/ID/view link.");
    }

    public async Task<DownloadedBackup> DownloadAsync(string fileIdOrLink, string destination,
        Action<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, long maxBytes = 1L << 40)
    {
        var fileId = ParseFileId(fileIdOrLink);
        destination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(destination)!;
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("The download destination already exists.");
        var account = new GoogleTokenProvider(store, http);
        var identity = account.Account ?? throw new DriveException(DriveFailure.Authorization, "Connect Google Drive on this client before downloading a backup.");
        var api = new DriveApi(http, account, identity.Key);
        var remote = await api.GetFileAsync(fileId, cancellationToken)
            ?? throw new DriveException(DriveFailure.Permanent, "The Google Drive backup is missing or unavailable to this account.");
        if (remote.Id != fileId || remote.Trashed || remote.MimeType != "application/zip" || remote.Size <= 0 || remote.Size > maxBytes ||
            !Protocol.ValidSecret(remote.Properties.GetValueOrDefault("autobackSha256")) ||
            !Guid.TryParseExact(remote.Properties.GetValueOrDefault("autobackJob"), "N", out var jobId) || jobId == Guid.Empty ||
            !Guid.TryParseExact(remote.Properties.GetValueOrDefault("autobackAgent"), "N", out var agentId) || agentId == Guid.Empty)
            throw new DriveException(DriveFailure.Permanent, "The selected Drive file is not a supported AutoBack ZIP or exceeds the download limit.");
        if (remote.Md5 is null) throw new DriveException(DriveFailure.Retryable, "Google Drive has not provided the backup checksum yet. Try again shortly.");
        Directory.CreateDirectory(parent);
        BackupPlan.CheckNoReparse(parent);
        if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < remote.Size)
            throw new IOException("There is insufficient free space to download the selected backup.");
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        var created = false;
        try
        {
            progress?.Invoke(new(0, remote.Size, "Downloading the selected backup directly from Google Drive."));
            using var response = await api.SendAsync(() => new(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media"), cancellationToken);
            if (response.Content.Headers.ContentLength is { } advertised && advertised != remote.Size)
                throw new InvalidDataException("Drive download length differs from the selected backup metadata.");
            long total = 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                created = true;
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[128 * 1024];
                var lastProgress = Environment.TickCount64;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (read > remote.Size - total || read > maxBytes - total) throw new InvalidDataException("Drive download exceeded its expected size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    sha.AppendData(buffer, 0, read);
                    md5.AppendData(buffer, 0, read);
                    total += read;
                    if (Environment.TickCount64 - lastProgress >= 200)
                    { progress?.Invoke(new(total, remote.Size, "Downloading and hashing the cloud archive.")); lastProgress = Environment.TickCount64; }
                }
                if (total != remote.Size) throw new InvalidDataException("The cloud download is truncated.");
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            var sha256 = Convert.ToHexString(sha.GetHashAndReset());
            if (!sha256.Equals(remote.Properties["autobackSha256"], StringComparison.OrdinalIgnoreCase) ||
                !Convert.ToHexString(md5.GetHashAndReset()).Equals(remote.Md5, StringComparison.OrdinalIgnoreCase) ||
                (remote.Sha256 is not null && !sha256.Equals(remote.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The downloaded archive failed Google Drive checksum verification.");
            var current = await api.GetFileAsync(fileId, cancellationToken);
            if (current is null || current.Id != fileId || current.Trashed || current.MimeType != remote.MimeType || current.Size != remote.Size || !string.Equals(current.Md5, remote.Md5, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current.Sha256, remote.Sha256, StringComparison.OrdinalIgnoreCase) ||
                current.Properties.GetValueOrDefault("autobackSha256") != remote.Properties["autobackSha256"] ||
                current.Properties.GetValueOrDefault("autobackJob") != jobId.ToString("N") || current.Properties.GetValueOrDefault("autobackAgent") != agentId.ToString("N"))
                throw new InvalidDataException("The Google Drive backup changed during download.");
            cancellationToken.ThrowIfCancellationRequested();
            BackupPlan.CheckNoReparse(parent);
            File.Move(partial, destination);
            created = false;
            progress?.Invoke(new(total, remote.Size, "Cloud archive downloaded and verified."));
            return new(destination, fileId, jobId, agentId, sha256, total);
        }
        finally
        {
            if (created && File.Exists(partial))
            {
                try { File.Delete(partial); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
