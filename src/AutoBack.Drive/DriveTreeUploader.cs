using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBack.Backup;
using AutoBack.Connection;

namespace AutoBack.Drive;

public sealed class DriveTreeUploader(ProtectedStore store, HttpClient http, Guid agentId, string agentName)
{
    public const string IndexFile = "drive-tree-index.dat";
    public const long ManifestLimit = 64L * 1024 * 1024;

    public async Task<TreeUploadResult> UploadAsync(Guid jobId, string archivePath, string accountKey,
        Action<UploadCheckpoint> checkpoint, Action<UploadProgress> progress, CancellationToken token)
    {
        var vault = EncryptionVault.Load(store);
        using var input = ArchiveFile.OpenRead(archivePath, vault.Resolve);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var source = await RestoreEngine.ReadManifestAsync(archive, new(), token);
        if (source.JobId != jobId || source.AgentId != agentId) throw new InvalidDataException("The staged backup belongs to another job.");
        await LocalBackupEngine.VerifyAsync(archivePath, source, token, vault.Resolve);
        using (var raw = File.OpenRead(archivePath))
        {
            var hashes = await DriveBlobTransfer.HashAsync(raw, token);
            checkpoint(new(Sha256: hashes.Sha256, Md5: hashes.Md5, Length: raw.Length));
        }
        ArchiveKey? encryptionKey = null;
        if (input is not FileStream)
        {
            using var raw = File.OpenRead(archivePath);
            var header = new byte[32]; raw.ReadExactly(header);
            encryptionKey = vault.Resolve(new Guid(header.AsSpan(16, 16).ToArray()));
        }
        var api = new DriveApi(http, new(store, http), accountKey);
        var manager = new DriveFolderManager(store, api, agentId, agentName);
        var folders = await manager.EnsureAsync(accountKey, token);
        var device = manager.DeviceId;
        var indexes = store.Load<Dictionary<string, TreeIndex>>(IndexFile) ?? [];
        indexes.TryGetValue(accountKey, out var index);
        var folderRemote = await api.GetFileAsync(folders.ClientId, token) ?? throw new InvalidDataException("The PC backup folder disappeared.");
        if (index is not null && index.Snapshot.FolderId != folders.ClientId) index = null;
        if (index is null && folderRemote.Properties.GetValueOrDefault("autobackManifest") is { } manifestId)
        {
            var snapshot = await DriveTreeReader.ReadSnapshotAsync(api, folderRemote, store.DirectoryPath, token);
            index = new(snapshot, manifestId, new());
        }
        if (index is not null && !index.Ready && index.Snapshot.JobId != jobId && index.Snapshot.AgentId == agentId)
        {
            // A remotely recovered ready snapshot is a baseline, not an unfinished local transfer.
            if (folderRemote.Properties.GetValueOrDefault("autobackState") != "ready" || folderRemote.Properties.GetValueOrDefault("autobackJob") != index.Snapshot.JobId.ToString("N"))
                throw new DriveException(DriveFailure.Permanent, "An earlier incremental backup is unfinished. Retry that backup before starting another cloud update.");
        }
        var old = index?.Snapshot;
        var roots = old?.Roots.ToList() ?? [];
        var directories = (old?.Directories ?? []).ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
        var files = (old?.Files ?? []).ToDictionary(f => f.Content.Path, StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        var archivePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rootMappings = source.Roots ?? [new(source.Source, "")];
        foreach (var mapping in rootMappings)
        {
            var sourceKey = Protocol.Hash(Path.GetFullPath(mapping.Source).ToUpperInvariant());
            var root = roots.SingleOrDefault(r => r.SourceKey == sourceKey);
            if (root is null)
            {
                var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(mapping.Source));
                if (string.IsNullOrEmpty(leaf)) leaf = "Drive-" + Path.GetPathRoot(mapping.Source)![0];
                RestoreEngine.ValidateRelativePath(leaf);
                var label = leaf; var suffix = 2;
                while (roots.Any(r => string.Equals(r.Label, label, StringComparison.OrdinalIgnoreCase))) label = leaf + " (" + suffix++ + ")";
                root = new(sourceKey, label); roots.Add(root);
            }
            await AddDirectory(root.Label);
            string Relative(string path) => root.Label + "/" + (mapping.Prefix.Length == 0 ? path : path[(mapping.Prefix.Length + 1)..]);
            bool Belongs(string path) => mapping.Prefix.Length == 0 || path.StartsWith(mapping.Prefix + "/", StringComparison.Ordinal);
            foreach (var directory in source.Directories.Where(Belongs)) await AddDirectory(Relative(directory));
            foreach (var file in source.Files.Where(f => Belongs(f.Path)))
            {
                var path = Relative(file.Path); current.Add(path, file with { Path = path }); archivePaths.Add(path, file.Path);
            }
        }
        var stageDirectory = Path.Combine(store.DirectoryPath, "tree-staging", jobId.ToString("N"));
        Directory.CreateDirectory(stageDirectory); BackupPlan.CheckNoReparse(stageDirectory);
        foreach (var pair in current)
        {
            var content = pair.Value;
            files.TryGetValue(pair.Key, out var previous);
            if (previous is not null && previous.Content.Sha256 == content.Sha256 && previous.Content.Length == content.Length &&
                previous.KeyId == encryptionKey?.Id && previous.Encrypted == (encryptionKey is not null))
            { files[pair.Key] = previous with { Content = content }; continue; }
            var id = previous?.Id ?? await api.GenerateIdAsync(token);
            files[pair.Key] = new(content, id, Parent(pair.Key), encryptionKey is not null, encryptionKey?.Id, new(FileId: id));
        }
        var conflicting = directories.Keys.FirstOrDefault(path => files.ContainsKey(path));
        if (conflicting is not null)
            throw new DriveException(DriveFailure.Permanent, "A retained backup path conflicts with a new file/folder: " + DriveHttp.SafeMessage(conflicting) + ". Rename the new source item before backing up; the retained copy was preserved.");
        // Re-pairing retires job reports, not the installation's data. A fresh backup may reuse a staged
        // transfer only when its current source bytes/key match; no retired job is queued or replayed.
        if (old is not null && old.AgentId != agentId && old.DeviceId == device)
        {
            foreach (var file in files.Values.Where(f => current.ContainsKey(f.Content.Path) && f.Transfer.SessionUri is not null))
            {
                var name = Protocol.Hash(file.Content.Path.ToUpperInvariant()) + ".bin";
                var priorPath = Path.Combine(store.DirectoryPath, "tree-staging", old.JobId.ToString("N"), name);
                var newPath = Path.Combine(stageDirectory, name);
                if (!File.Exists(newPath) && File.Exists(priorPath))
                {
                    BackupPlan.CheckNoReparse(Path.GetDirectoryName(priorPath)!);
                    if ((File.GetAttributes(priorPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("The staged transfer is a link.");
                    using var previous = File.OpenRead(priorPath);
                    var hashes = await DriveBlobTransfer.HashAsync(previous, token);
                    if (hashes.Sha256 != file.Transfer.Sha256 || previous.Length != file.Transfer.Length)
                        throw new InvalidDataException("The preserved staged transfer changed after re-pairing.");
                    System.IO.File.Copy(priorPath, newPath);
                }
            }
        }
        var snapshotCurrent = new TreeSnapshot(1, device, folders.ClientId, jobId, agentId, source.CreatedUtc, source.Source, roots,
            directories.Values.OrderBy(d => d.Path, StringComparer.Ordinal).ToList(), files.Values.OrderBy(f => f.Content.Path, StringComparer.Ordinal).ToList());
        var manifestFileId = index?.ManifestId ?? await api.GenerateIdAsync(token);
        var sameJob = index?.Snapshot.JobId == jobId;
        index = new(snapshotCurrent, manifestFileId, sameJob ? index!.ManifestTransfer : new(), false);
        Save();
        // Prepare changed file bytes locally before touching the live cloud folder.
        foreach (var file in index.Snapshot.Files.Where(f => current.ContainsKey(f.Content.Path) && f.Transfer.Sha256 is null).ToArray())
        {
            var path = StagePath(file);
            await StageAsync(archive.GetEntry("files/" + archivePaths[file.Content.Path])!, file, path, encryptionKey, token);
            using var staged = File.OpenRead(path);
            var hashes = await DriveBlobTransfer.HashAsync(staged, token);
            Replace(file with { Transfer = new(file.Id, Md5: hashes.Md5, Sha256: hashes.Sha256, Length: staged.Length, FolderId: file.ParentId) });
        }
        index.Snapshot.Validate();
        var folderProperties = new Dictionary<string, string>(folderRemote.Properties)
        { ["autobackState"] = "updating", ["autobackJob"] = jobId.ToString("N"), ["autobackAgent"] = agentId.ToString("N") };
        await api.PatchAsync(folders.ClientId, new { appProperties = folderProperties }, token);
        // Keep the whole retained directory set. A deleted child folder is recreated with a fresh ID and remapped descendants.
        foreach (var directory in index.Snapshot.Directories.OrderBy(d => d.Path.Count(c => c == '/')).ThenBy(d => d.Path, StringComparer.Ordinal).ToArray())
        {
            var item = index.Snapshot.Directories.Single(d => d.Path == directory.Path);
            var remote = await api.GetFileAsync(item.Id, token);
            if (remote?.Trashed == true || (remote is null && old?.Directories.Any(d => d.Id == item.Id) == true))
            {
                var replacement = await api.GenerateIdAsync(token);
                index = index with { Snapshot = index.Snapshot with
                {
                    Directories = index.Snapshot.Directories.Select(d => d.Id == item.Id ? d with { Id = replacement } : d.ParentId == item.Id ? d with { ParentId = replacement } : d).ToList(),
                    Files = index.Snapshot.Files.Select(f => f.ParentId == item.Id ? f with { ParentId = replacement } : f).ToList()
                } }; Save(); item = index.Snapshot.Directories.Single(d => d.Path == item.Path);
            }
            await api.EnsureFolderAsync(item.Id, Leaf(item.Path), item.ParentId, "tree-directory", agentId, token, device, Protocol.Hash(item.Path.ToUpperInvariant()));
        }
        var transfer = new DriveBlobTransfer(api);
        long bytesSent = 0; int uploadedFiles = 0, unchangedFiles = 0;
        var estimated = checked(index.Snapshot.Files.Sum(f => f.Transfer.Length) + ManifestLimit);
        void Progress(long bytes) { bytesSent += bytes; progress(new(bytesSent, Math.Max(bytesSent, estimated), "Uploading only new or changed files directly to the PC's Drive folder.")); }
        foreach (var pending in index.Snapshot.Files.ToArray())
        {
            token.ThrowIfCancellationRequested();
            var file = pending;
            var remote = await api.GetFileAsync(file.Id, token);
            var blob = Blob(file);
            if (remote is not null && !remote.Trashed && !DriveBlobTransfer.Owned(remote, blob)) throw new InvalidDataException("A backup file has unexpected ownership or parent metadata.");
            if (remote is not null && DriveBlobTransfer.Matches(remote, file.Transfer))
            {
                if (remote.Name != blob.Name) await api.PatchAsync(remote.Id, new { name = blob.Name }, token);
                unchangedFiles++; continue;
            }
            if (!current.ContainsKey(file.Content.Path))
                throw new DriveException(DriveFailure.Permanent, "A file retained only in Drive is missing or changed: " + DriveHttp.SafeMessage(file.Content.Path) + ". Restore it from Drive Trash, then retry; AutoBack will not silently drop it.");
            if (remote is null || remote.Trashed)
            {
                // A newly allocated ID with an existing saved session is still pending, not a deleted file.
                if (remote?.Trashed == true || (remote is null && old?.Files.Any(f => f.Id == file.Id && f.Transfer.SessionUri is null) == true))
                {
                    var id = await api.GenerateIdAsync(token);
                    file = file with { Id = id, Transfer = file.Transfer with { FileId = id, SessionUri = null, FolderId = file.ParentId } }; Replace(file); blob = Blob(file);
                }
            }
            var stagedPath = StagePath(file);
            if (!File.Exists(stagedPath))
            {
                if (file.Encrypted && file.Transfer.SessionUri is not null)
                    throw new InvalidDataException("An encrypted staged file is missing. Its saved upload was preserved; restore the staging file before retrying.");
                await StageAsync(archive.GetEntry("files/" + archivePaths[file.Content.Path])!, file, stagedPath, file.KeyId is { } key ? vault.Resolve(key) : null, token);
                using var staged = File.OpenRead(stagedPath);
                var hashes = await DriveBlobTransfer.HashAsync(staged, token);
                file = file with { Transfer = file.Transfer with { Sha256 = hashes.Sha256, Md5 = hashes.Md5, Length = staged.Length, SessionUri = null } }; Replace(file);
            }
            var captured = file;
            var completed = await transfer.UploadAsync(blob, stagedPath, file.Transfer, state => Replace(captured with { Transfer = state }), Progress, token);
            file = file with { Transfer = completed with { SessionUri = null } }; Replace(file); uploadedFiles++;
            File.Delete(stagedPath);
        }
        // Publish the recovery index last. A partially updated folder is never advertised as restorable.
        var published = index.Snapshot with { Files = index.Snapshot.Files.Select(f => f with { Transfer = f.Transfer with { SessionUri = null } }).ToList() };
        published.Validate();
        var manifestPath = Path.Combine(stageDirectory, "index.json");
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(published, Protocol.Json);
        if (jsonBytes.Length > ManifestLimit) throw new InvalidDataException("The incremental recovery index exceeds its size limit.");
        if (!File.Exists(manifestPath)) System.IO.File.WriteAllBytes(manifestPath, jsonBytes);
        else if (!File.ReadAllBytes(manifestPath).SequenceEqual(jsonBytes))
        {
            // A retry may have repaired a deleted child. Never reuse a session for different index bytes.
            File.Delete(manifestPath); System.IO.File.WriteAllBytes(manifestPath, jsonBytes);
            index = index with { ManifestTransfer = new() }; Save();
        }
        var manifestRemote = await api.GetFileAsync(index.ManifestId, token);
        if (manifestRemote?.Trashed == true || (manifestRemote is null && index.ManifestTransfer.Sha256 is not null && index.ManifestTransfer.SessionUri is null))
        { index = index with { ManifestId = await api.GenerateIdAsync(token), ManifestTransfer = new() }; Save(); }
        var manifestBlob = new DriveBlob(index.ManifestId, "_AutoBack.json", folders.ClientId, new()
        { ["autobackRole"] = "tree-manifest", ["autobackDevice"] = device, ["autobackPath"] = "manifest" });
        var manifestTransfer = await transfer.UploadAsync(manifestBlob, manifestPath, index.ManifestTransfer,
            state => { index = index with { ManifestTransfer = state }; Save(); }, Progress, token);
        await DriveTreeReader.VerifyFilesAsync(api, published, token);
        folderProperties["autobackState"] = "ready"; folderProperties["autobackManifest"] = index.ManifestId;
        folderProperties["autobackManifestSha256"] = manifestTransfer.Sha256!;
        await api.PatchAsync(folders.ClientId, new { appProperties = folderProperties }, token);
        var confirmed = await api.GetFileAsync(folders.ClientId, token);
        if (confirmed is null || confirmed.Trashed || confirmed.Properties.GetValueOrDefault("autobackManifestSha256") != manifestTransfer.Sha256 ||
            confirmed.Properties.GetValueOrDefault("autobackState") != "ready" || confirmed.Properties.GetValueOrDefault("autobackJob") != jobId.ToString("N"))
            throw new InvalidDataException("Drive did not confirm the completed incremental backup.");
        index = index with { Snapshot = published, ManifestTransfer = manifestTransfer with { SessionUri = null }, Ready = true }; Save();
        File.Delete(manifestPath);
        // Remove only recognized scratch files in this exact job's private staging directory, including
        // a file left after a lost completion response. Never recurse into source/backup folders.
        foreach (var scratch in Directory.EnumerateFiles(stageDirectory))
        {
            var name = Path.GetFileName(scratch); var parts = name.Split('.');
            var owned = parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit) &&
                ((parts.Length == 2 && parts[1] == "bin") || (parts.Length == 4 && parts[1] == "bin" && Guid.TryParseExact(parts[2], "N", out _) && parts[3] == "partial"));
            if (owned && (File.GetAttributes(scratch) & FileAttributes.ReparsePoint) == 0) File.Delete(scratch);
        }
        if (!Directory.EnumerateFileSystemEntries(stageDirectory).Any()) Directory.Delete(stageDirectory);
        progress(new(bytesSent, bytesSent, "Incremental Drive backup verified."));
        return new(folders.ClientId, bytesSent, uploadedFiles, unchangedFiles, index.Snapshot.Files.Count(f => !current.ContainsKey(f.Content.Path)));

        async Task AddDirectory(string path)
        {
            if (!directories.ContainsKey(path)) directories[path] = new(path, await api.GenerateIdAsync(token), Parent(path));
        }
        string Parent(string path) { var slash = path.LastIndexOf('/'); return slash < 0 ? folders.ClientId : directories[path[..slash]].Id; }
        void Save() { indexes[accountKey] = index!; store.Save(IndexFile, indexes); }
        void Replace(TreeFile file)
        { index = index! with { Snapshot = index.Snapshot with { Files = index.Snapshot.Files.Select(f => f.Content.Path == file.Content.Path ? file : f).ToList() } }; Save(); }
        string StagePath(TreeFile file) => Path.Combine(stageDirectory, Protocol.Hash(file.Content.Path.ToUpperInvariant()) + ".bin");
        DriveBlob Blob(TreeFile file) => new(file.Id, Leaf(file.Content.Path) + (file.Encrypted ? ".abk" : ""), file.ParentId, new()
        { ["autobackRole"] = "tree-file", ["autobackDevice"] = device, ["autobackPath"] = Protocol.Hash(file.Content.Path.ToUpperInvariant()), ["autobackPlainSha256"] = file.Content.Sha256 });
    }

    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static async Task StageAsync(ZipArchiveEntry entry, TreeFile file, string destination, ArchiveKey? key, CancellationToken token)
    {
        if (File.Exists(destination))
        {
            using var saved = key is null ? File.OpenRead(destination) : ArchiveFile.OpenRead(destination, _ => key);
            if (saved.Length != file.Content.Length || Convert.ToHexString(await SHA256.HashDataAsync(saved, token)) != file.Content.Sha256)
                throw new InvalidDataException("A staged incremental file failed verification.");
            return;
        }
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                using var encrypted = key is null ? null : new EncryptedWriteStream(output, key);
                using var content = entry.Open();
                await content.CopyToAsync((Stream?)encrypted ?? output, 128 * 1024, token);
                encrypted?.Finish(); await output.FlushAsync(token); output.Flush(true);
            }
            using (var check = key is null ? File.OpenRead(partial) : ArchiveFile.OpenRead(partial, _ => key))
                if (check.Length != file.Content.Length || Convert.ToHexString(await SHA256.HashDataAsync(check, token)) != file.Content.Sha256)
                    throw new InvalidDataException("The staged file differs from the verified source snapshot.");
            File.Move(partial, destination);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
