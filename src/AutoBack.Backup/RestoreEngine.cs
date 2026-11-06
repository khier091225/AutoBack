using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoBack.Connection;

namespace AutoBack.Backup;

public sealed record RestoreLimits(int MaxEntries = 250_000, long MaxTotalBytes = 1L << 40, long MaxManifestBytes = 64L * 1024 * 1024);
public sealed record RestoreProgress(string State, int Percent, int Files, long Bytes, string Detail);
public sealed record RestoreSource(string Kind, string Description, Guid? ExpectedJobId = null, Guid? ExpectedAgentId = null);
public sealed record RestoreResult(Guid OperationId, Guid BackupJobId, Guid BackupAgentId, DateTimeOffset VerifiedUtc,
    string SourceKind, string SourceDescription, string Destination, string PayloadDirectory, string ArchiveSha256,
    int Files, int Directories, long Bytes, string State = "Completed", List<ManifestRoot>? Roots = null);

public sealed partial class RestoreEngine
{
#if NETFRAMEWORK
    private static readonly Regex LegacyDeviceName = new(@"^(CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[0-9\u00b9\u00b2\u00b3]|LPT[0-9\u00b9\u00b2\u00b3])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static Regex DeviceName() => LegacyDeviceName;
#else
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();
#endif

    public static void ValidateRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path!.Length > 4096 || path.Contains('\\') || path.StartsWith('/') || path.Any(char.IsControl))
            throw new InvalidDataException("The backup contains an unsafe relative path.");
        foreach (var segment in path.Split('/'))
            if (segment is "" or "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || DeviceName().IsMatch(segment.Split('.')[0].TrimEnd(' ')))
                throw new InvalidDataException("The backup contains an unsupported Windows file name.");
    }

    public static string NormalizeDestination(string destination)
    {
        if (!Path.IsPathFullyQualified(destination) || destination.StartsWith(@"\\", StringComparison.Ordinal) ||
            !BackupStates.ValidText(destination, 1024)) throw new ArgumentException("Choose a full local destination folder path.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var root = Path.GetPathRoot(full)!;
        if (new DriveInfo(root).DriveType == DriveType.Network) throw new ArgumentException("Choose a local restore destination, not a mapped network drive.");
        if (full.Length == root.Length) throw new ArgumentException("Choose a new folder below a local drive, not the drive root.");
        ValidateRelativePath(full[root.Length..].Replace('\\', '/'));
        BackupPlan.CheckNoReparse(full);
        return full;
    }

    public static async Task<BackupManifest> ReadManifestAsync(ZipArchive archive, RestoreLimits limits, CancellationToken token = default)
    {
        if (archive.Entries.Count > limits.MaxEntries) throw new InvalidDataException("The archive exceeds the configured entry limit.");
        var entries = archive.Entries.Where(e => e.FullName == "manifest.json").ToArray();
        if (entries.Length != 1 || entries[0].Length <= 0 || entries[0].Length > limits.MaxManifestBytes)
            throw new InvalidDataException("The archive must contain one bounded AutoBack manifest.");
        CheckEntryType(entries[0], directory: false);
        using var stream = entries[0].Open();
        // Reading at most the declared bounded size also prevents a forged manifest entry from expanding indefinitely.
        using var buffer = new MemoryStream();
        var bytes = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(bytes, token)) > 0)
        {
            if (buffer.Length + read > limits.MaxManifestBytes || buffer.Length + read > entries[0].Length)
                throw new InvalidDataException("The manifest is larger than declared.");
            buffer.Write(bytes, 0, read);
        }
        if (buffer.Length != entries[0].Length) throw new InvalidDataException("The manifest is truncated.");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(buffer.ToArray(), Protocol.Json)
            ?? throw new InvalidDataException("The backup manifest is empty.");
        ValidateManifest(manifest, limits);
        return manifest;
    }

    public static void ValidateManifest(BackupManifest manifest, RestoreLimits limits)
    {
        if (manifest.FormatVersion is not (1 or 2) || manifest.JobId == Guid.Empty || manifest.AgentId == Guid.Empty ||
            !BackupStates.ValidText(manifest.Source, 1024) || !Path.IsPathFullyQualified(manifest.Source) ||
            manifest.Files is null || manifest.Directories is null ||
            (long)manifest.Files.Count + manifest.Directories.Count + 1 > limits.MaxEntries)
            throw new InvalidDataException("Unsupported or invalid AutoBack backup manifest.");
        if (manifest.FormatVersion == 1 && manifest.Roots is { Count: > 0 }) throw new InvalidDataException("Single-folder manifests cannot declare additional source roots.");
        if (manifest.FormatVersion == 2)
        {
            if (manifest.Roots is null || manifest.Roots.Count is < 2 or > 20 || manifest.Roots.Any(r => r is null || !BackupStates.ValidText(r.Source, 1024) || !Path.IsPathFullyQualified(r.Source)) ||
                manifest.Roots.Select(r => r.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Roots.Count || manifest.Source != manifest.Roots[0].Source)
                throw new InvalidDataException("Invalid multi-folder backup source mapping.");
            var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in manifest.Roots)
            {
                ValidateRelativePath(root.Prefix);
                if (root.Prefix.Contains('/') || !prefixes.Add(root.Prefix) || !manifest.Directories.Contains(root.Prefix, StringComparer.Ordinal))
                    throw new InvalidDataException("Invalid or duplicate multi-folder restore prefix.");
            }
            if (manifest.Directories.Any(d => d is null || !prefixes.Contains(d.Split('/')[0])) || manifest.Files.Any(f => f is null || f.Path is null || !f.Path.Contains('/') || !prefixes.Contains(f.Path.Split('/')[0])))
                throw new InvalidDataException("A multi-folder archive entry is outside its declared source roots.");
        }
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in manifest.Directories)
        {
            ValidateRelativePath(directory);
            if (!all.Add(directory)) throw new InvalidDataException("The manifest contains duplicate or case-colliding paths.");
            directories.Add(directory);
        }
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null) throw new InvalidDataException("The manifest contains a null file record.");
            ValidateRelativePath(file.Path);
            if (!all.Add(file.Path) || file.Length < 0 || !Protocol.ValidSecret(file.Sha256) || file.LastWriteUtc.Kind != DateTimeKind.Utc)
                throw new InvalidDataException("The manifest contains an invalid file record or conflicting path.");
            if (file.Length > limits.MaxTotalBytes - total) throw new InvalidDataException("The manifest exceeds the restore byte limit.");
            total += file.Length;
        }
        foreach (var path in all)
        {
            var separator = path.LastIndexOf('/');
            if (separator >= 0 && !directories.Contains(path[..separator]))
                throw new InvalidDataException("A parent directory is missing or conflicts with a file.");
        }
    }

    public async Task<RestoreResult> RestoreAsync(string archivePath, string destination, RestoreSource source,
        Action<RestoreProgress>? progress = null, CancellationToken cancellationToken = default, RestoreLimits? limits = null, Guid? operationId = null, Func<Guid, ArchiveKey>? keys = null)
    {
        limits ??= new();
        destination = NormalizeDestination(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("The restore destination already exists. Choose a new folder; existing files are never overwritten.");
        var operation = operationId ?? Guid.NewGuid();
        if (operation == Guid.Empty) throw new ArgumentException("A restore operation ID cannot be empty.");
        var parent = Path.GetDirectoryName(destination)!;
        var staging = Path.Combine(parent, ".autoback-restore-" + operation.ToString("N") + ".partial");
        var stagingCreated = false;
        progress?.Invoke(new("Validating", 0, 0, 0, "Validating the archive manifest and all restore paths."));
        try
        {
            using var input = ArchiveFile.OpenRead(Path.GetFullPath(archivePath), keys);
            await ArchiveFile.AuthenticateAsync(input, cancellationToken);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            var manifest = await ReadManifestAsync(archive, limits, cancellationToken);
            if ((source.ExpectedJobId.HasValue && source.ExpectedJobId != manifest.JobId) ||
                (source.ExpectedAgentId.HasValue && source.ExpectedAgentId != manifest.AgentId))
                throw new InvalidDataException("The downloaded archive does not match the selected Google Drive backup identity.");
            foreach (var sourcePath in manifest.Roots?.Select(r => r.Source) ?? [manifest.Source])
            {
                var originalSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
                if (BackupPlan.Contains(originalSource, destination) || BackupPlan.Contains(destination, originalSource))
                    throw new IOException("Restore into a separate folder outside every original source tree.");
            }
            var expected = new Dictionary<string, (bool Directory, ManifestFile? File)>(StringComparer.Ordinal)
            { ["manifest.json"] = (false, null) };
            foreach (var directory in manifest.Directories) expected.Add("files/" + directory + "/", (true, null));
            foreach (var file in manifest.Files) expected.Add("files/" + file.Path, (false, file));
            var zipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (!zipNames.Add(entry.FullName) || !expected.TryGetValue(entry.FullName, out var metadata))
                    throw new InvalidDataException("The ZIP contains duplicate, unexpected, or unsafe entries.");
                CheckEntryType(entry, metadata.Directory);
                if (metadata.Directory ? entry.Length != 0 : metadata.File is not null && entry.Length != metadata.File.Length)
                    throw new InvalidDataException("A ZIP entry has an unexpected declared size.");
            }
            if (zipNames.Count != expected.Count) throw new InvalidDataException("The ZIP is missing files or directories declared in its manifest.");
            var total = manifest.Files.Sum(f => f.Length);
            Directory.CreateDirectory(parent);
            BackupPlan.CheckNoReparse(parent);
            var drive = new DriveInfo(Path.GetPathRoot(destination)!);
            if (total > drive.AvailableFreeSpace) throw new IOException("There is insufficient free space for the restored files.");
            if (Directory.Exists(staging) || File.Exists(staging)) throw new IOException("The restore staging path already exists.");
            Directory.CreateDirectory(staging);
            stagingCreated = true;
            BackupPlan.CheckNoReparse(staging);
            var payload = Path.Combine(staging, "files");
            Directory.CreateDirectory(payload);
            foreach (var directory in manifest.Directories.OrderBy(p => p.Count(c => c == '/')))
                Directory.CreateDirectory(SafePath(payload, directory));
            long restoredBytes = 0;
            var restoredFiles = 0;
            var lastProgress = Protocol.MonotonicMilliseconds;
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = SafePath(payload, file.Path);
                BackupPlan.CheckNoReparse(Path.GetDirectoryName(target)!);
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                using (var content = archive.GetEntry("files/" + file.Path)!.Open())
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[128 * 1024];
                    long written = 0;
                    int read;
                    while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        if (read > file.Length - written || read > limits.MaxTotalBytes - restoredBytes)
                            throw new InvalidDataException("An archive entry expanded beyond its permitted size.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        hash.AppendData(buffer, 0, read);
                        written += read;
                        restoredBytes += read;
                        if (Protocol.MonotonicMilliseconds - lastProgress >= 200)
                        {
                            progress?.Invoke(new("Restoring", total == 0 ? 1 : 1 + (int)((double)restoredBytes / total * 78), restoredFiles, restoredBytes, "Restoring " + Short(file.Path)));
                            lastProgress = Protocol.MonotonicMilliseconds;
                        }
                    }
                    if (written != file.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Restored content failed checksum verification: " + Short(file.Path));
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                }
                File.SetLastWriteTimeUtc(target, file.LastWriteUtc);
                restoredFiles++;
            }
            progress?.Invoke(new("Verifying", 80, restoredFiles, restoredBytes, "Reading back the restored files and verifying the complete file set."));
            await VerifyPayloadAsync(payload, manifest, cancellationToken);
            using var raw = File.OpenRead(archivePath);
            var archiveHash = Convert.ToHexString(await SHA256.HashDataAsync(raw, cancellationToken));
            var result = new RestoreResult(operation, manifest.JobId, manifest.AgentId, DateTimeOffset.UtcNow, source.Kind,
                source.Description, destination, Path.Combine(destination, "files"), archiveHash, restoredFiles, manifest.Directories.Count, restoredBytes, Roots: manifest.Roots);
            await WriteJsonAsync(Path.Combine(staging, "manifest.json"), manifest, cancellationToken);
            await WriteJsonAsync(Path.Combine(staging, "restore-report.json"), result, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(new("Publishing", 99, restoredFiles, restoredBytes, "Publishing the verified recovery folder."));
            cancellationToken.ThrowIfCancellationRequested();
            BackupPlan.CheckNoReparse(parent);
            BackupPlan.CheckNoReparse(staging);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("The destination appeared during restore. Existing files were preserved.");
            Directory.Move(staging, destination);
            stagingCreated = false;
            progress?.Invoke(new("Completed", 100, restoredFiles, restoredBytes, "Restore completed; every restored file passed SHA-256 verification."));
            return result;
        }
        finally
        {
            // This is the exact, newly created sibling staging directory owned by this operation.
            if (stagingCreated && Path.GetDirectoryName(Path.GetFullPath(staging)) == parent &&
                Path.GetFileName(staging) == ".autoback-restore-" + operation.ToString("N") + ".partial" && Directory.Exists(staging))
            {
                try
                {
                    if ((File.GetAttributes(staging) & FileAttributes.ReparsePoint) == 0) Directory.Delete(staging, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static async Task<RestoreResult> VerifyRestoredAsync(string destination, CancellationToken token = default, RestoreLimits? limits = null)
    {
        limits ??= new();
        destination = NormalizeDestination(destination);
        BackupPlan.CheckNoReparse(destination);
        var manifestFile = Path.Combine(destination, "manifest.json");
        if ((File.GetAttributes(manifestFile) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The restored manifest is a link.");
        if (new FileInfo(manifestFile).Length > limits.MaxManifestBytes) throw new InvalidDataException("Restored manifest exceeds its limit.");
        using var manifestInput = File.OpenRead(manifestFile);
        var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(manifestInput, Protocol.Json, token)
            ?? throw new InvalidDataException("The restored folder has no manifest.");
        ValidateManifest(manifest, limits);
        var reportPath = Path.Combine(destination, "restore-report.json");
        if ((File.GetAttributes(reportPath) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The restore report is a link.");
        if (new FileInfo(reportPath).Length > 64 * 1024) throw new InvalidDataException("Restore report is too large.");
        using var reportInput = File.OpenRead(reportPath);
        var report = await JsonSerializer.DeserializeAsync<RestoreResult>(reportInput, Protocol.Json, token)
            ?? throw new InvalidDataException("The restored folder has no restore report.");
        if (report.State != "Completed" || report.OperationId == Guid.Empty || report.BackupJobId != manifest.JobId || report.BackupAgentId != manifest.AgentId ||
            !Protocol.ValidSecret(report.ArchiveSha256) || report.Files != manifest.Files.Count || report.Directories != manifest.Directories.Count || report.Bytes != manifest.Files.Sum(f => f.Length))
            throw new InvalidDataException("Restore report and manifest do not agree.");
        if (JsonSerializer.Serialize(report.Roots, Protocol.Json) != JsonSerializer.Serialize(manifest.Roots, Protocol.Json))
            throw new InvalidDataException("Restore report source folders do not match the manifest.");
        await VerifyPayloadAsync(Path.Combine(destination, "files"), manifest, token);
        return report with { VerifiedUtc = DateTimeOffset.UtcNow, Destination = destination, PayloadDirectory = Path.Combine(destination, "files") };
    }

    private static async Task VerifyPayloadAsync(string payload, BackupManifest manifest, CancellationToken token)
    {
        BackupPlan.CheckNoReparse(payload);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(payload);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A restored path is a symbolic link or junction.");
                var relative = Path.GetRelativePath(payload, path).Replace('\\', '/');
                if ((attributes & FileAttributes.Directory) != 0) { directories.Add(relative); pending.Push(path); }
                else files.Add(relative);
            }
        }
        if (!files.SetEquals(manifest.Files.Select(f => f.Path)) || !directories.SetEquals(manifest.Directories))
            throw new InvalidDataException("The restored file/directory set does not match the manifest.");
        foreach (var file in manifest.Files)
        {
            var target = SafePath(payload, file.Path);
            BackupPlan.CheckNoReparse(Path.GetDirectoryName(target)!);
            using var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
            if (input.Length != file.Length || !Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A restored file is missing, changed, or corrupt: " + Short(file.Path));
        }
    }

    private static void CheckEntryType(ZipArchiveEntry entry, bool directory)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
            (unixType != 0 && unixType != (directory ? 0x4000 : 0x8000)))
            throw new InvalidDataException("ZIP symbolic links, devices, and other special entries are unsupported.");
    }

    private static string SafePath(string root, string relative)
    {
        ValidateRelativePath(relative);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A restore path escapes the recovery folder.");
        return full;
    }
    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(output, value, Protocol.Json, token);
        await output.FlushAsync(token);
        output.Flush(flushToDisk: true);
    }
    private static string Short(string path) => path.Length <= 700 ? path : path[..700];
}
