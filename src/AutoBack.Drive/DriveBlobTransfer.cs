using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AutoBack.Connection;

namespace AutoBack.Drive;

public sealed record DriveBlob(string Id, string Name, string ParentId, Dictionary<string, string> Properties);

// Resumable transfers of individual files, with bounded memory on both modern and 32-bit clients.
public sealed class DriveBlobTransfer(DriveApi api)
{
    public static bool Owned(RemoteFile remote, DriveBlob blob) => remote.Id == blob.Id &&
        remote.MimeType == "application/octet-stream" && remote.Parents.Contains(blob.ParentId) &&
        remote.Properties.GetValueOrDefault("autobackDevice") == blob.Properties.GetValueOrDefault("autobackDevice") &&
        remote.Properties.GetValueOrDefault("autobackRole") == blob.Properties.GetValueOrDefault("autobackRole") &&
        remote.Properties.GetValueOrDefault("autobackPath") == blob.Properties.GetValueOrDefault("autobackPath");

    public static bool Matches(RemoteFile remote, UploadCheckpoint state) => !remote.Trashed && remote.Size == state.Length &&
        string.Equals(remote.Md5, state.Md5, StringComparison.OrdinalIgnoreCase) &&
        (remote.Sha256 is null || string.Equals(remote.Sha256, state.Sha256, StringComparison.OrdinalIgnoreCase)) &&
        remote.Properties.GetValueOrDefault("autobackSha256") == state.Sha256;

    public async Task<UploadCheckpoint> UploadAsync(DriveBlob blob, string path, UploadCheckpoint saved,
        Action<UploadCheckpoint> checkpoint, Action<long> uploaded, CancellationToken token)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
        var hashes = await HashAsync(input, token);
        if (saved.Sha256 is not null && (saved.Sha256 != hashes.Sha256 || saved.Length != input.Length))
            throw new InvalidDataException("The staged upload changed. Its saved session was preserved for inspection.");
        var state = saved with { FileId = blob.Id, FolderId = blob.ParentId, Sha256 = hashes.Sha256, Md5 = hashes.Md5, Length = input.Length };
        checkpoint(state);
        var remote = await api.GetFileAsync(blob.Id, token);
        if (remote is not null)
        {
            if (remote.Trashed || !Owned(remote, blob)) throw new InvalidDataException("A cloud file's ownership or location changed. No other file was overwritten.");
            if (Matches(remote, state)) return state;
        }
        if (state.SessionUri is null)
        {
            var props = new Dictionary<string, string>(blob.Properties) { ["autobackSha256"] = hashes.Sha256 };
            var metadata = new Dictionary<string, object> { ["name"] = blob.Name, ["mimeType"] = "application/octet-stream", ["appProperties"] = props };
            if (remote is null) { metadata["id"] = blob.Id; metadata["parents"] = new[] { blob.ParentId }; }
            var url = "https://www.googleapis.com/upload/drive/v3/files" + (remote is null ? "" : "/" + blob.Id) + "?uploadType=resumable";
            using var response = await api.SendAsync(() =>
            {
                var request = new HttpRequestMessage(remote is null ? HttpMethod.Post : new HttpMethod("PATCH"), url)
                { Content = JsonContent.Create(metadata) };
                request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
                request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", input.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return request;
            }, token, 409);
            if ((int)response.StatusCode == 409)
                throw new DriveException(DriveFailure.Retryable, "Checking a cloud file created before a lost response. Retry will verify its saved ID.");
            var session = response.Headers.Location?.AbsoluteUri;
            if (!DriveHttp.SafeSessionUri(session)) throw new DriveException(DriveFailure.Permanent, "Google returned an unexpected upload endpoint. No file bytes were sent.");
            state = state with { SessionUri = session }; checkpoint(state);
        }
        if (!DriveHttp.SafeSessionUri(state.SessionUri)) throw new InvalidDataException("Invalid saved file upload endpoint.");
        using var status = await api.SendAsync(() => Request(state.SessionUri!, [], null, state.Length), token, 308, 404);
        CheckSession(status);
        var offset = status.IsSuccessStatusCode ? state.Length : Offset(status, state.Length);
        // A zero-byte file still needs a final, empty upload request.
        var empty = state.Length == 0 && !status.IsSuccessStatusCode;
        var buffer = new byte[DriveUploader.ChunkSize];
        while (offset < state.Length || empty)
        {
            token.ThrowIfCancellationRequested();
            input.Position = offset;
            var size = (int)Math.Min(buffer.Length, state.Length - offset);
            if (size > 0) await input.ReadExactlyAsync(buffer.AsMemory(0, size), token);
            using var response = await api.SendAsync(() => Request(state.SessionUri!, buffer.AsSpan(0, size).ToArray(), empty ? null : offset, state.Length), token, 308, 404);
            CheckSession(response);
            var next = response.IsSuccessStatusCode ? state.Length : Offset(response, state.Length);
            if (!response.IsSuccessStatusCode && next <= offset) throw new DriveException(DriveFailure.Retryable, "Drive did not acknowledge more file bytes. The saved session will be checked before retrying.");
            uploaded(next - offset); offset = next; empty = false;
        }
        remote = await api.GetFileAsync(blob.Id, token);
        if (remote is null || !Owned(remote, blob) || !Matches(remote, state))
        {
            checkpoint(state with { SessionUri = null });
            throw new InvalidDataException("An uploaded file failed Drive size or checksum verification. The local backup is preserved.");
        }
        return state;

        void CheckSession(HttpResponseMessage response)
        {
            if ((int)response.StatusCode != 404) return;
            checkpoint(state with { SessionUri = null });
            throw new DriveException(DriveFailure.Retryable, "The file upload session expired. Retry will reuse the same Drive file ID.");
        }
    }

    public async Task DownloadAsync(RemoteFile remote, string path, long maxBytes, CancellationToken token)
    {
        if (remote.Trashed || remote.Size < 0 || remote.Size > maxBytes || remote.Md5 is null || !Protocol.ValidSecret(remote.Properties.GetValueOrDefault("autobackSha256")))
            throw new InvalidDataException("The cloud file is missing a verified checksum or exceeds the download limit.");
        using var response = await api.SendAsync(() => new(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{remote.Id}?alt=media"), token);
        using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[128 * 1024];
        long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            total = checked(total + read);
            if (total > remote.Size || total > maxBytes) throw new InvalidDataException("A cloud file exceeded its declared size.");
            await output.WriteAsync(buffer.AsMemory(0, read), token); sha.AppendData(buffer, 0, read); md5.AppendData(buffer, 0, read);
        }
        var state = new UploadCheckpoint(remote.Id, Md5: Convert.ToHexString(md5.GetHashAndReset()), Sha256: Convert.ToHexString(sha.GetHashAndReset()), Length: total);
        if (total != remote.Size || !Matches(remote, state)) throw new InvalidDataException("A downloaded file failed checksum verification.");
        var current = await api.GetFileAsync(remote.Id, token);
        if (current is null || !Matches(current, state) || current.MimeType != remote.MimeType ||
            !current.Parents.SequenceEqual(remote.Parents) || current.Properties.GetValueOrDefault("autobackDevice") != remote.Properties.GetValueOrDefault("autobackDevice"))
            throw new InvalidDataException("A cloud file changed during download.");
        await output.FlushAsync(token); output.Flush(true);
    }

    public static async Task<(string Sha256, string Md5)> HashAsync(Stream input, CancellationToken token)
    {
        input.Position = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[128 * 1024]; int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0) { sha.AppendData(buffer, 0, read); md5.AppendData(buffer, 0, read); }
        return (Convert.ToHexString(sha.GetHashAndReset()), Convert.ToHexString(md5.GetHashAndReset()));
    }

    private static HttpRequestMessage Request(string session, byte[] data, long? offset, long total)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new("application/octet-stream");
        content.Headers.ContentRange = offset is null ? new ContentRangeHeaderValue(total) : new(offset.Value, offset.Value + data.Length - 1, total);
        return new(HttpMethod.Put, session) { Content = content };
    }

    private static long Offset(HttpResponseMessage response, long total)
    {
        if (!response.Headers.TryGetValues("Range", out var values)) return 0;
        var range = values.Single();
        if (!range.StartsWith("bytes=0-", StringComparison.Ordinal) || !long.TryParse(range[8..], out var last) || last < 0 || last >= total)
            throw new InvalidDataException("Drive returned an invalid file upload range.");
        return last + 1;
    }
}
