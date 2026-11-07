using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBack.Backup;
using AutoBack.Connection;

namespace AutoBack.Drive;

public static class DriveTreeReader
{
    public static async Task<TreeSnapshot> ReadSnapshotAsync(DriveApi api, RemoteFile folder, string temporaryRoot, CancellationToken token)
    {
        if (folder.Trashed || folder.MimeType != "application/vnd.google-apps.folder" || folder.Properties.GetValueOrDefault("autobackRole") != "client" ||
            folder.Properties.GetValueOrDefault("autobackState") != "ready" || !CloudStates.ValidId(folder.Properties.GetValueOrDefault("autobackManifest")))
            throw new InvalidDataException("This Drive folder has no completed incremental backup. Finish or retry its backup before restoring.");
        var manifest = await api.GetFileAsync(folder.Properties["autobackManifest"], token);
        if (manifest is null || manifest.Trashed || manifest.MimeType != "application/octet-stream" || !manifest.Parents.Contains(folder.Id) ||
            manifest.Properties.GetValueOrDefault("autobackRole") != "tree-manifest" ||
            manifest.Properties.GetValueOrDefault("autobackDevice") != folder.Properties.GetValueOrDefault("autobackDevice") ||
            manifest.Properties.GetValueOrDefault("autobackSha256") != folder.Properties.GetValueOrDefault("autobackManifestSha256"))
            throw new InvalidDataException("The Drive recovery index has changed or is missing.");
        Directory.CreateDirectory(temporaryRoot); BackupPlan.CheckNoReparse(temporaryRoot);
        var temporary = Path.Combine(temporaryRoot, "tree-index-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            await new DriveBlobTransfer(api).DownloadAsync(manifest, temporary, DriveTreeUploader.ManifestLimit, token);
            using var input = File.OpenRead(temporary);
            var snapshot = await JsonSerializer.DeserializeAsync<TreeSnapshot>(input, Protocol.Json, token) ?? throw new InvalidDataException("The Drive recovery index is empty.");
            snapshot.Validate();
            if (snapshot.FolderId != folder.Id || snapshot.DeviceId != folder.Properties.GetValueOrDefault("autobackDevice") ||
                snapshot.JobId.ToString("N") != folder.Properties.GetValueOrDefault("autobackJob") || snapshot.AgentId.ToString("N") != folder.Properties.GetValueOrDefault("autobackAgent"))
                throw new InvalidDataException("The Drive folder and its recovery index identify different backups.");
            return snapshot;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task VerifyFilesAsync(DriveApi api, TreeSnapshot snapshot, CancellationToken token)
    {
        snapshot.Validate();
        var folder = await api.GetFileAsync(snapshot.FolderId, token);
        if (folder is null || folder.Trashed || folder.Properties.GetValueOrDefault("autobackDevice") != snapshot.DeviceId)
            throw new InvalidDataException("The PC backup folder is missing or changed.");
        foreach (var directory in snapshot.Directories)
        {
            var remote = await api.GetFileAsync(directory.Id, token);
            if (remote is null || remote.Trashed || remote.MimeType != "application/vnd.google-apps.folder" || !remote.Parents.Contains(directory.ParentId) || remote.Name != directory.Path[(directory.Path.LastIndexOf('/') + 1)..] ||
                remote.Properties.GetValueOrDefault("autobackDevice") != snapshot.DeviceId || remote.Properties.GetValueOrDefault("autobackRole") != "tree-directory" ||
                remote.Properties.GetValueOrDefault("autobackPath") != Protocol.Hash(directory.Path.ToUpperInvariant()))
                throw new InvalidDataException("A backup directory is missing or changed: " + DriveHttp.SafeMessage(directory.Path));
        }
        foreach (var file in snapshot.Files) await GetFileAsync(api, snapshot, file, token);
    }

    public static async Task<RemoteFile> GetFileAsync(DriveApi api, TreeSnapshot snapshot, TreeFile file, CancellationToken token)
    {
        var remote = await api.GetFileAsync(file.Id, token);
        if (remote is null || !DriveBlobTransfer.Matches(remote, file.Transfer) || remote.MimeType != "application/octet-stream" || !remote.Parents.Contains(file.ParentId) ||
            remote.Name != file.Content.Path[(file.Content.Path.LastIndexOf('/') + 1)..] + (file.Encrypted ? ".abk" : "") ||
            remote.Properties.GetValueOrDefault("autobackDevice") != snapshot.DeviceId || remote.Properties.GetValueOrDefault("autobackRole") != "tree-file" ||
            remote.Properties.GetValueOrDefault("autobackPath") != Protocol.Hash(file.Content.Path.ToUpperInvariant()) ||
            remote.Properties.GetValueOrDefault("autobackPlainSha256") != file.Content.Sha256)
            throw new InvalidDataException("A cloud backup file is missing or changed: " + DriveHttp.SafeMessage(file.Content.Path));
        return remote;
    }

    public static async Task<DownloadedBackup> DownloadAsync(ProtectedStore store, DriveApi api, RemoteFile folder, string destination,
        Action<DownloadProgress>? progress, CancellationToken token, long maxBytes)
    {
        var snapshot = await ReadSnapshotAsync(api, folder, store.DirectoryPath, token);
        var total = snapshot.Files.Sum(f => f.Content.Length);
        if (total > maxBytes) throw new InvalidDataException("The folder backup exceeds the download limit.");
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent); BackupPlan.CheckNoReparse(parent);
        if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < total)
            throw new IOException("There is insufficient free space to download this backup.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        var blobPath = Path.Combine(store.DirectoryPath, "tree-download-" + Guid.NewGuid().ToString("N") + ".partial");
        var vault = EncryptionVault.Load(store);
        long copied = 0;
        try
        {
            // This ZIP exists only as local restore staging; Drive stores individual files, never a backup ZIP.
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var directory in snapshot.Directories) archive.CreateEntry("files/" + directory.Path + "/");
                    foreach (var file in snapshot.Files)
                    {
                        var remote = await GetFileAsync(api, snapshot, file, token);
                        await new DriveBlobTransfer(api).DownloadAsync(remote, blobPath, file.Transfer.Length, token);
                        using (var content = file.Encrypted ? ArchiveFile.OpenRead(blobPath, vault.Resolve) : File.OpenRead(blobPath))
                        {
                            if (content.Length != file.Content.Length || Convert.ToHexString(await SHA256.HashDataAsync(content, token)) != file.Content.Sha256)
                                throw new InvalidDataException("A downloaded file failed plaintext verification.");
                            content.Position = 0;
                            using var entry = archive.CreateEntry("files/" + file.Content.Path, CompressionLevel.Fastest).Open();
                            await content.CopyToAsync(entry, 128 * 1024, token);
                        }
                        File.Delete(blobPath); copied += file.Content.Length;
                        progress?.Invoke(new(copied, total, "Downloading and verifying the current PC folder backup."));
                    }
                    using var manifestOutput = archive.CreateEntry("manifest.json", CompressionLevel.Fastest).Open();
                    await JsonSerializer.SerializeAsync(manifestOutput, snapshot.Manifest, Protocol.Json, token);
                }
                await output.FlushAsync(token); output.Flush(true);
            }
            await VerifyFilesAsync(api, snapshot, token);
            var current = await api.GetFileAsync(folder.Id, token);
            if (current is null || current.Trashed || current.Properties.GetValueOrDefault("autobackState") != "ready" ||
                current.Properties.GetValueOrDefault("autobackJob") != snapshot.JobId.ToString("N") ||
                current.Properties.GetValueOrDefault("autobackManifestSha256") != folder.Properties.GetValueOrDefault("autobackManifestSha256"))
                throw new InvalidDataException("The folder backup changed during recovery. Retry after the backup completes.");
            await LocalBackupEngine.VerifyAsync(temporary, snapshot.Manifest, token);
            using var raw = File.OpenRead(temporary);
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(raw, token));
            var length = raw.Length; raw.Dispose();
            BackupPlan.CheckNoReparse(parent); File.Move(temporary, destination);
            return new(destination, folder.Id, snapshot.JobId, snapshot.AgentId, sha, length);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(blobPath)) File.Delete(blobPath);
        }
    }
}
