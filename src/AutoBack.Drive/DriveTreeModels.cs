using AutoBack.Backup;
using AutoBack.Connection;

namespace AutoBack.Drive;

public sealed record TreeRoot(string SourceKey, string Label);
public sealed record TreeDirectory(string Path, string Id, string ParentId);
public sealed record TreeFile(ManifestFile Content, string Id, string ParentId, bool Encrypted, Guid? KeyId, UploadCheckpoint Transfer);
public sealed record TreeSnapshot(int Version, string DeviceId, string FolderId, Guid JobId, Guid AgentId, DateTimeOffset CreatedUtc,
    string Source, List<TreeRoot> Roots, List<TreeDirectory> Directories, List<TreeFile> Files)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public BackupManifest Manifest => new(1, JobId, AgentId, CreatedUtc, Source, Directories.Select(d => d.Path).ToList(), Files.Select(f => f.Content).ToList());

    public void Validate()
    {
        if (Version != 1 || !Guid.TryParseExact(DeviceId, "N", out var device) || device == Guid.Empty || !CloudStates.ValidId(FolderId) ||
            Roots is null || Directories is null || Files is null || Roots.Any(r => !Protocol.ValidSecret(r.SourceKey)))
            throw new InvalidDataException("Invalid incremental Drive backup index.");
        RestoreEngine.ValidateManifest(Manifest, new());
        var dirs = Directories.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal) { FolderId };
        foreach (var root in Roots) RestoreEngine.ValidateRelativePath(root.Label);
        foreach (var directory in Directories)
        {
            if (!CloudStates.ValidId(directory.Id) || !ids.Add(directory.Id) || directory.ParentId != Parent(directory.Path))
                throw new InvalidDataException("Invalid Drive backup directory mapping.");
        }
        foreach (var file in Files)
        {
            if (!CloudStates.ValidId(file.Id) || !ids.Add(file.Id) || file.ParentId != Parent(file.Content.Path) ||
                file.Transfer.FileId != file.Id || !Protocol.ValidSecret(file.Transfer.Sha256) ||
                file.Transfer.Md5 is not { Length: 32 } || !file.Transfer.Md5.All(Uri.IsHexDigit) || file.Transfer.Length < 0 ||
                (file.Encrypted && (file.KeyId is null || file.KeyId == Guid.Empty)) || (!file.Encrypted && file.KeyId is not null))
                throw new InvalidDataException("Invalid Drive backup file mapping.");
        }

        string Parent(string path)
        {
            var slash = path.LastIndexOf('/');
            if (slash < 0) return FolderId;
            if (!dirs.TryGetValue(path[..slash], out var parent)) throw new InvalidDataException("A Drive directory mapping is missing.");
            return parent.Id;
        }
    }
}
public sealed record TreeIndex(TreeSnapshot Snapshot, string ManifestId, UploadCheckpoint ManifestTransfer, bool Ready = false);
public sealed record TreeUploadResult(string FolderId, long UploadedBytes, int UploadedFiles, int UnchangedFiles, int RetainedFiles);
