using System.Net.Http.Json;
using AutoBack.Connection;

namespace AutoBack.Drive;

public sealed class DriveRetention(ProtectedStore store, HttpClient http, Guid agentId)
{
    public async Task<int> PruneAsync(IReadOnlyList<SavedUpload> uploads, int keep, Action<Guid> trashed, CancellationToken token = default)
    {
        if (keep == 0) return 0;
        if (keep is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(keep));
        var tokens = new GoogleTokenProvider(store, http);
        var account = tokens.Account ?? throw new DriveException(DriveFailure.Authorization, "Sign in to the original Google account before cloud retention.");
        var folders = store.Load<Dictionary<string, DriveFolders>>("drive-folders.dat");
        if (folders is null || !folders.TryGetValue(account.Key, out var folder)) return 0;
        var api = new DriveApi(http, tokens, account.Key);
        // Journal order is completion-discovery order, independent of arbitrary file names and remote listing order.
        var completed = uploads.Where(u => u.AccountKey == account.Key && u.Report.State == "Completed" && !u.Report.Trashed).Reverse().ToArray();
        foreach (var survivor in completed.Take(keep))
        {
            var remote = await GetOwned(survivor);
            if (remote.Trashed) throw new InvalidDataException("A cloud copy selected for retention is already trashed; older copies were preserved.");
        }
        var removed = 0;
        foreach (var old in completed.Skip(keep))
        {
            var remote = await GetOwned(old);
            if (!remote.Trashed)
            {
                using var response = await api.SendAsync(() => new(HttpMethod.Patch, $"https://www.googleapis.com/drive/v3/files/{remote.Id}")
                { Content = JsonContent.Create(new { trashed = true }) }, token);
                remote = await GetOwned(old);
                if (!remote.Trashed) throw new InvalidDataException("Google Drive did not confirm retention trashing.");
            }
            trashed(old.JobId); removed++;
        }
        return removed;

        async Task<RemoteFile> GetOwned(SavedUpload upload)
        {
            var id = upload.Report.FileId;
            if (!CloudStates.ValidId(id) || !Protocol.ValidSecret(upload.Checkpoint.Sha256) || upload.Checkpoint.Md5 is null)
                throw new InvalidDataException("Missing confirmed upload identity; cloud retention was skipped.");
            var file = await api.GetFileAsync(id!, token) ?? throw new InvalidDataException("A retained cloud backup is missing; older copies were preserved.");
            if (file.Id != id || !file.Parents.Contains(folder.ClientId) || file.MimeType is not ("application/zip" or "application/octet-stream") ||
                file.Properties.GetValueOrDefault("autobackAgent") != agentId.ToString("N") || file.Properties.GetValueOrDefault("autobackJob") != upload.JobId.ToString("N") ||
                file.Properties.GetValueOrDefault("autobackSha256") != upload.Checkpoint.Sha256 || file.Size != upload.Checkpoint.Length ||
                !string.Equals(file.Md5, upload.Checkpoint.Md5, StringComparison.OrdinalIgnoreCase) ||
                (file.Sha256 is not null && !string.Equals(file.Sha256, upload.Checkpoint.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Cloud retention refused a file with changed identity, folder or checksum.");
            return file;
        }
    }
}
