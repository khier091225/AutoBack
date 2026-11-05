using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBack.Backup;
using AutoBack.Connection;

namespace AutoBack.Drive;

public sealed record DriveFolders(string RootId, string ClientId);

public sealed class DriveUploader(ProtectedStore store, GoogleTokenProvider tokens, HttpClient http, Guid agentId, string agentName)
{
    public const int ChunkSize = 1024 * 1024; // A multiple of Drive's 256 KiB chunk unit.

    public async Task<UploadResult> UploadAsync(Guid jobId, string archivePath, string accountKey, UploadCheckpoint saved,
        Action<UploadCheckpoint> checkpoint, Action<UploadProgress> progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath)) throw new DriveException(DriveFailure.Permanent, "The local archive is missing. Restore the local file or create a new backup.");
        await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var encrypted = Path.GetExtension(archivePath).Equals(".abk", StringComparison.OrdinalIgnoreCase);
        var mime = encrypted ? "application/octet-stream" : "application/zip";
        var vault = EncryptionVault.Load(store);
        using (var archiveInput = ArchiveFile.OpenRead(archivePath, vault.Resolve))
        using (var archive = new ZipArchive(archiveInput, ZipArchiveMode.Read))
        {
            var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("The local ZIP has no AutoBack manifest.");
            await using var manifestStream = entry.Open();
            var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream, Protocol.Json, cancellationToken)
                ?? throw new InvalidDataException("The local backup manifest is empty.");
            if (manifest.JobId != jobId || manifest.AgentId != agentId) throw new InvalidDataException("The local archive belongs to a different backup job.");
            await LocalBackupEngine.VerifyAsync(archivePath, manifest, cancellationToken, vault.Resolve);
        }
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[ChunkSize];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0) { sha.AppendData(buffer, 0, read); md5.AppendData(buffer, 0, read); }
        var sha256 = Convert.ToHexString(sha.GetHashAndReset());
        var checksum = Convert.ToHexString(md5.GetHashAndReset());
        if (saved.Sha256 is not null && (saved.Sha256 != sha256 || saved.Length != input.Length))
            throw new DriveException(DriveFailure.Permanent, "The local archive changed after upload started. The existing cloud version will not be overwritten.");
        var state = saved with { Sha256 = sha256, Md5 = checksum, Length = input.Length };
        checkpoint(state);
        var api = new DriveApi(http, tokens, accountKey);
        var folders = await EnsureFoldersAsync(api, accountKey, cancellationToken);
        if (state.FileId is null) { state = state with { FileId = await api.GenerateIdAsync(cancellationToken) }; checkpoint(state); }
        var remote = await api.GetFileAsync(state.FileId!, cancellationToken);
        if (remote is not null && (state.SessionUri is null || (remote.Size == state.Length && remote.Md5 is not null)))
            return Verify(remote, state, folders.ClientId, jobId);
        if (state.SessionUri is null)
        {
            var metadata = new Dictionary<string, object>
            {
                ["id"] = state.FileId!, ["name"] = $"{jobId:N}-{Path.GetFileName(archivePath)}", ["mimeType"] = mime,
                ["parents"] = new[] { folders.ClientId },
                ["appProperties"] = new Dictionary<string, string>
                {
                    ["autobackJob"] = jobId.ToString("N"), ["autobackAgent"] = agentId.ToString("N"), ["autobackSha256"] = sha256
                }
            };
            using var started = await api.SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable");
                request.Content = JsonContent.Create(metadata);
                request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", mime);
                request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", input.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return request;
            }, cancellationToken, 409);
            if ((int)started.StatusCode == 409)
                return Verify(await api.GetFileAsync(state.FileId!, cancellationToken) ?? throw new DriveException(DriveFailure.Retryable, "Waiting for the existing Google Drive file to become available."), state, folders.ClientId, jobId);
            var session = started.Headers.Location?.AbsoluteUri;
            if (!DriveHttp.SafeSessionUri(session)) throw new DriveException(DriveFailure.Permanent, "Google returned an unexpected upload endpoint. No archive bytes were sent.");
            state = state with { SessionUri = session };
            checkpoint(state); // Persist the session before sending archive data.
        }
        if (!DriveHttp.SafeSessionUri(state.SessionUri)) throw new InvalidDataException("The saved Drive upload session endpoint is invalid.");
        using var status = await api.SendAsync(() => UploadRequest(state.SessionUri!, [], null, input.Length), cancellationToken, 308, 404);
        if ((int)status.StatusCode == 404)
        {
            checkpoint(state with { SessionUri = null });
            throw new DriveException(DriveFailure.Retryable, "The Drive upload session expired. A new session will reuse this backup's file ID.");
        }
        long offset = status.IsSuccessStatusCode ? input.Length : Offset(status, input.Length);
        progress(new(offset, input.Length, "Uploading the local archive directly to Google Drive."));
        while (offset < input.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            input.Position = offset;
            var length = (int)Math.Min(ChunkSize, input.Length - offset);
            await input.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken);
            var chunk = buffer.AsSpan(0, length).ToArray();
            var start = offset;
            using var response = await api.SendAsync(() => UploadRequest(state.SessionUri!, chunk, start, input.Length), cancellationToken, 308, 404);
            if ((int)response.StatusCode == 404)
            {
                checkpoint(state with { SessionUri = null });
                throw new DriveException(DriveFailure.Retryable, "The Drive upload session expired. The local archive is safe and upload will restart.");
            }
            var next = response.IsSuccessStatusCode ? input.Length : Offset(response, input.Length);
            if (next <= offset) throw new DriveException(DriveFailure.Retryable, "Google did not acknowledge more upload bytes. The session will be checked before retrying.");
            offset = next;
            progress(new(offset, input.Length, offset == input.Length ? "Verifying the uploaded file size and checksum." : "Uploading directly from this client to Google Drive."));
        }
        remote = await api.GetFileAsync(state.FileId!, cancellationToken)
            ?? throw new DriveException(DriveFailure.Retryable, "Waiting for Google Drive to confirm the uploaded file.");
        return Verify(remote, state, folders.ClientId, jobId);
    }

    private async Task<DriveFolders> EnsureFoldersAsync(DriveApi api, string accountKey, CancellationToken token)
    {
        var cache = store.Load<Dictionary<string, DriveFolders>>("drive-folders.dat") ?? [];
        if (!cache.TryGetValue(accountKey, out var folders))
        {
            folders = new(await api.FindRootAsync(token) ?? await api.GenerateIdAsync(token), await api.GenerateIdAsync(token));
            cache[accountKey] = folders;
            store.Save("drive-folders.dat", cache);
        }
        await api.EnsureFolderAsync(folders.RootId, "AutoBack Backups", null, "root", agentId, token);
        await api.EnsureFolderAsync(folders.ClientId, $"{agentName}-{agentId:N}", folders.RootId, "client", agentId, token);
        return folders;
    }

    private UploadResult Verify(RemoteFile remote, UploadCheckpoint state, string folderId, Guid jobId)
    {
        if (!remote.Trashed && remote.Size == state.Length && remote.Md5 is null)
            throw new DriveException(DriveFailure.Retryable, "Waiting for Google Drive to provide the uploaded file checksum.");
        if (remote.Id != state.FileId || remote.Trashed || remote.MimeType is not ("application/zip" or "application/octet-stream") || remote.Size != state.Length ||
            !string.Equals(remote.Md5, state.Md5, StringComparison.OrdinalIgnoreCase) ||
            (remote.Sha256 is not null && !string.Equals(remote.Sha256, state.Sha256, StringComparison.OrdinalIgnoreCase)) ||
            !remote.Parents.Contains(folderId) || remote.Properties.GetValueOrDefault("autobackJob") != jobId.ToString("N") ||
            remote.Properties.GetValueOrDefault("autobackAgent") != agentId.ToString("N") || remote.Properties.GetValueOrDefault("autobackSha256") != state.Sha256)
            throw new DriveException(DriveFailure.Permanent, "Google Drive file verification failed. The local backup is preserved; cloud completion has not been confirmed.");
        return new(remote.Id, $"https://drive.google.com/file/d/{remote.Id}/view", remote.Size);
    }

    private static HttpRequestMessage UploadRequest(string session, byte[] bytes, long? offset, long total)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/zip");
        content.Headers.ContentRange = offset is null ? new ContentRangeHeaderValue(total) : new(offset.Value, offset.Value + bytes.Length - 1, total);
        return new(HttpMethod.Put, session) { Content = content };
    }

    private static long Offset(HttpResponseMessage response, long total)
    {
        if (!response.Headers.TryGetValues("Range", out var ranges)) return 0;
        var value = ranges.Single();
        if (!value.StartsWith("bytes=0-", StringComparison.Ordinal) || !long.TryParse(value[8..], out var last) || last < 0 || last >= total)
            throw new DriveException(DriveFailure.Permanent, "Google returned an invalid upload byte range.");
        return last + 1;
    }
}
