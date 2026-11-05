using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBack.Connection;

namespace AutoBack.Backup;

public sealed record ManifestFile(string Path, long Length, DateTime LastWriteUtc, string Sha256);
public sealed record BackupManifest(int FormatVersion, Guid JobId, Guid AgentId, DateTimeOffset CreatedUtc, string Source,
    List<string> Directories, List<ManifestFile> Files);
public sealed record BackupProgress(string State, int Percent, long BytesCopied, long TotalBytes, int FilesCopied, int TotalFiles, string Detail);
public sealed record BackupResult(string ArchivePath, long Bytes, int Files);

public sealed class LocalBackupEngine
{
    private sealed record SourceFile(string FullName, string RelativeName, long Length, DateTime ModifiedUtc);
    private sealed record Inventory(List<string> Directories, List<SourceFile> Files);

    public async Task<BackupResult> CreateAsync(BackupPlan plan, Guid agentId, BackupCommand command,
        Action<BackupProgress> report, CancellationToken cancellationToken = default)
    {
        if (agentId == Guid.Empty || command.JobId == Guid.Empty || command.PlanId != plan.PlanId)
            throw new InvalidDataException("The backup request does not match the client's configured folders.");
        plan.Validate();
        report(new("Preparing", 0, 0, 0, 0, 0, "Scanning source files."));
        var inventory = Scan(plan.Source, cancellationToken);
        var totalBytes = inventory.Files.Aggregate(0L, (sum, file) => checked(sum + file.Length));
        report(new("Copying", 1, 0, totalBytes, 0, inventory.Files.Count, "Copying the scanned source files."));
        var versionDirectory = Path.Combine(plan.Destination, "AutoBack", agentId.ToString("N"), command.JobId.ToString("N"));
        Directory.CreateDirectory(versionDirectory);
        BackupPlan.CheckNoReparse(versionDirectory);
        var archivePath = Path.Combine(versionDirectory, $"backup-{command.CreatedUtc:yyyyMMddTHHmmssZ}.zip");
        if (archivePath.Length > 1024) throw new IOException("The destination path is too long for backup reporting. Choose a shorter local destination.");
        var partialPath = archivePath + ".partial";
        if (File.Exists(archivePath) || File.Exists(partialPath))
            throw new IOException("Output already exists for this job. Retry as a new backup to preserve existing files.");
        long copied = 0;
        var filesCopied = 0;
        var manifest = new BackupManifest(1, command.JobId, agentId, command.CreatedUtc, plan.Source, inventory.Directories, []);
        var lastProgress = Environment.TickCount64;
        var createdPartial = false;
        try
        {
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                createdPartial = true;
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var directory in inventory.Directories) archive.CreateEntry("files/" + directory + "/");
                    foreach (var file in inventory.Files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        BackupPlan.CheckNoReparse(Path.GetDirectoryName(file.FullName)!);
                        if ((File.GetAttributes(file.FullName) & FileAttributes.ReparsePoint) != 0) throw new IOException("A source file became a symbolic link: " + file.RelativeName);
                        await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        if (input.Length != file.Length || File.GetLastWriteTimeUtc(file.FullName) != file.ModifiedUtc)
                            throw new IOException("A source file changed during backup: " + file.RelativeName);
                        var entry = archive.CreateEntry("files/" + file.RelativeName, CompressionLevel.Fastest);
                        if (file.ModifiedUtc.Year is >= 1980 and <= 2107) entry.LastWriteTime = new DateTimeOffset(file.ModifiedUtc, TimeSpan.Zero);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        await using (var target = entry.Open())
                        {
                            var buffer = new byte[128 * 1024];
                            int read;
                            while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                            {
                                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                                hash.AppendData(buffer, 0, read);
                                copied += read;
                                if (Environment.TickCount64 - lastProgress >= 250)
                                {
                                    report(new("Copying", Percent(copied, totalBytes, 1, 84), copied, totalBytes, filesCopied, inventory.Files.Count, "Copying " + Short(file.RelativeName)));
                                    lastProgress = Environment.TickCount64;
                                }
                            }
                        }
                        if (File.GetLastWriteTimeUtc(file.FullName) != file.ModifiedUtc || input.Length != file.Length)
                            throw new IOException("A source file changed during backup: " + file.RelativeName);
                        manifest.Files.Add(new(file.RelativeName, file.Length, file.ModifiedUtc, Convert.ToHexString(hash.GetHashAndReset())));
                        filesCopied++;
                    }
                    // Detect additions/removals/metadata changes across the folder while files were being copied.
                    var finalInventory = Scan(plan.Source, cancellationToken);
                    if (!inventory.Directories.SequenceEqual(finalInventory.Directories) || !inventory.Files.SequenceEqual(finalInventory.Files))
                        throw new IOException("The source folder changed during backup. Close files being edited and start a new backup.");
                    await using var manifestStream = archive.CreateEntry("manifest.json", CompressionLevel.Fastest).Open();
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, Protocol.Json, cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            report(new("Verifying", 85, copied, totalBytes, filesCopied, inventory.Files.Count, "Verifying the saved archive against SHA-256 checksums."));
            await VerifyAsync(partialPath, manifest, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            BackupPlan.CheckNoReparse(versionDirectory);
            File.Move(partialPath, archivePath); // Publish only a closed, flushed, and verified archive. Never overwrite another version.
            return new(archivePath, copied, filesCopied);
        }
        finally
        {
            if (createdPartial && File.Exists(partialPath))
            {
                try { File.Delete(partialPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static async Task VerifyAsync(string archivePath, BackupManifest expected, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var names = archive.Entries.Select(e => e.FullName).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || archive.Entries.Count != expected.Files.Count + expected.Directories.Count + 1)
            throw new InvalidDataException("The saved archive has missing or duplicate entries.");
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("The saved archive has no manifest.");
        await using (var stream = manifestEntry.Open())
        {
            var actual = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, Protocol.Json, cancellationToken);
            if (actual is null || JsonSerializer.Serialize(actual, Protocol.Json) != JsonSerializer.Serialize(expected, Protocol.Json))
                throw new InvalidDataException("The saved manifest does not match this backup.");
        }
        foreach (var directory in expected.Directories)
            if (archive.GetEntry("files/" + directory + "/") is null) throw new InvalidDataException("A directory entry is missing.");
        foreach (var file in expected.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.GetEntry("files/" + file.Path) ?? throw new InvalidDataException("A file is missing from the saved archive.");
            if (entry.Length != file.Length) throw new InvalidDataException("A saved file has the wrong size: " + file.Path);
            await using var stream = entry.Open();
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(file.Sha256)))
                throw new InvalidDataException("Checksum verification failed: " + file.Path);
        }
    }

    private static Inventory Scan(string source, CancellationToken cancellationToken)
    {
        var directories = new List<string>();
        var files = new List<SourceFile>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new(source));
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackupPlan.CheckNoReparse(directory.FullName);
            foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links and junctions are not supported: " + entry.FullName);
                var relative = Path.GetRelativePath(source, entry.FullName).Replace('\\', '/');
                if (entry is DirectoryInfo child) { directories.Add(relative); pending.Push(child); }
                else if (entry is FileInfo file) files.Add(new(file.FullName, relative, file.Length, file.LastWriteTimeUtc));
            }
        }
        directories.Sort(StringComparer.Ordinal);
        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativeName, b.RelativeName));
        return new(directories, files);
    }

    private static int Percent(long value, long total, int start, int span) => total == 0 ? start : start + (int)Math.Min(span, (double)value / total * span);
    private static string Short(string text) => text.Length <= 800 ? text : text[..800];
}
