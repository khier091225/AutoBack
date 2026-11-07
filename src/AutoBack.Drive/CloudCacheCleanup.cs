using System.Security.Cryptography;
using AutoBack.Backup;
using AutoBack.Connection;

namespace AutoBack.Drive;

public sealed class CloudCacheCleanup(ProtectedStore store, HttpClient http, Guid agentId)
{
    public async Task<int> RunAsync(IReadOnlyList<LocalJob> jobs, IReadOnlyList<SavedUpload> uploads, Action<Guid> removed, CancellationToken token = default)
    {
        var candidates = jobs.Where(j => j.Target == BackupTargets.GoogleDrive && j.Acknowledged &&
            j.Report.State == "Completed" && !j.Report.ArchivePruned && uploads.Any(u => u.JobId == j.Command.JobId && u.Acknowledged && u.Report.State == "Completed" && !u.Report.Trashed)).ToArray();
        if (candidates.Length == 0) return 0;
        var tokens = new GoogleTokenProvider(store, http);
        var account = tokens.Account ?? throw new DriveException(DriveFailure.Authorization, "Reconnect Google Drive before confirming temporary archive cleanup.");
        var folders = store.Load<Dictionary<string, DriveFolders>>("drive-folders.dat");
        if (folders is null || !folders.TryGetValue(account.Key, out var folder)) throw new InvalidDataException("The confirmed Google Drive folder is missing; temporary archives were preserved.");
        var api = new DriveApi(http, tokens, account.Key);
        var expectedRoot = BackupPlan.CacheDirectory(store.DirectoryPath);
        var count = 0;
        foreach (var job in candidates)
        {
            token.ThrowIfCancellationRequested();
            var upload = uploads.Single(u => u.JobId == job.Command.JobId);
            if (upload.AccountKey != account.Key || job.CloudAccountKey != upload.AccountKey) continue;
            var path = Path.Combine(expectedRoot, "AutoBack", agentId.ToString("N"), job.Command.JobId.ToString("N"),
                $"backup-{job.Command.CreatedUtc:yyyyMMddTHHmmssZ}." + (job.Report.Encrypted ? "abk" : "zip"));
            if (job.CacheRoot != expectedRoot || job.Report.ArchivePath != path || upload.ArchivePath != path ||
                !Protocol.ValidSecret(job.Report.ArchiveSha256) || upload.Checkpoint.Sha256 != job.Report.ArchiveSha256 ||
                !CloudStates.ValidId(upload.Report.FileId) || upload.Checkpoint.Md5 is null)
                throw new InvalidDataException("Temporary cleanup refused a path or checksum outside the verified managed cache.");
            BackupPlan.CheckNoReparse(Path.GetDirectoryName(path)!);
            // Confirm the cloud copy again: account changes, manual trashing or missing remote files must preserve the cache.
            var remote = await api.GetFileAsync(upload.Report.FileId!, token);
            if (upload.FolderBased)
            {
                if (remote is null || remote.Trashed || remote.Id != folder.ClientId)
                    throw new InvalidDataException("The current PC folder is missing. The temporary archive was preserved.");
                var tree = await DriveTreeReader.ReadSnapshotAsync(api, remote, store.DirectoryPath, token);
                var current = uploads.SingleOrDefault(u => u.FolderBased && u.JobId == tree.JobId && u.AccountKey == account.Key && u.Acknowledged && u.Report is { State: "Completed", Superseded: false });
                if (current is null || (!upload.Report.Superseded && tree.JobId != upload.JobId))
                    throw new InvalidDataException("The current incremental backup is not acknowledged. Temporary archives were preserved.");
                await DriveTreeReader.VerifyFilesAsync(api, tree, token);
            }
            else if (remote is null || remote.Trashed || remote.Id != upload.Report.FileId || !remote.Parents.Contains(folder.ClientId) ||
                remote.Properties.GetValueOrDefault("autobackJob") != job.Command.JobId.ToString("N") ||
                remote.Properties.GetValueOrDefault("autobackAgent") != agentId.ToString("N") ||
                remote.Properties.GetValueOrDefault("autobackSha256") != job.Report.ArchiveSha256 || remote.Size != upload.Checkpoint.Length ||
                !string.Equals(remote.Md5, upload.Checkpoint.Md5, StringComparison.OrdinalIgnoreCase) ||
                (remote.Sha256 is not null && !remote.Sha256.Equals(job.Report.ArchiveSha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The cloud backup could not be reconfirmed. Temporary archive preserved for retry/recovery.");
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Temporary cleanup refused a linked archive.");
                using (var input = File.OpenRead(path))
                    if (input.Length != upload.Checkpoint.Length || Convert.ToHexString(await SHA256.HashDataAsync(input, token)) != job.Report.ArchiveSha256)
                        throw new InvalidDataException("The temporary archive changed; automatic cleanup was stopped.");
                File.Delete(path); // One exact owned file; original source folders and legacy/local backups are untouched.
            }
            removed(job.Command.JobId); count++;
            // Empty directories only; a crash before annotation is recovered by checking the missing file on the next pass.
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        return count;
    }
}
