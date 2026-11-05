using System.IO.Compression;
using System.Security.Cryptography;
using AutoBack.Connection;

namespace AutoBack.Backup;

public sealed record RetentionPolicy(int KeepLocal = 0, int KeepCloud = 0)
{
    public void Validate()
    {
        if (KeepLocal is < 0 or > 10000 || KeepCloud is < 0 or > 10000)
            throw new ArgumentException("Retention counts must be 0 (disabled) or 1–10000 successful versions.");
    }
    public static RetentionPolicy Load(ProtectedStore store)
    {
        var policy = store.Load<RetentionPolicy>("retention.dat") ?? new(); policy.Validate(); return policy;
    }
}

public static class LocalRetention
{
    public static async Task<int> PruneAsync(BackupPlan plan, Guid agentId, IReadOnlyList<LocalJob> jobs, int keep,
        ISet<Guid> protectedJobs, Func<Guid, ArchiveKey> keys, Action<Guid> pruned, CancellationToken token = default)
    {
        if (keep == 0) return 0;
        if (keep is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(keep));
        plan.Validate();
        var available = jobs.Where(j => j.Command.PlanId == plan.PlanId && j.Report.State == "Completed" && !j.Report.ArchivePruned)
            .OrderByDescending(j => j.Command.CreatedUtc).ThenByDescending(j => j.Command.JobId).ToArray();
        // Do not discard older recovery points when the copies we intend to keep cannot be verified.
        foreach (var survivor in available.Take(keep)) await VerifyOwned(survivor);
        var removed = 0;
        foreach (var old in available.Skip(keep).Where(j => !protectedJobs.Contains(j.Command.JobId)))
        {
            token.ThrowIfCancellationRequested();
            var path = OwnedPath(old);
            if (File.Exists(path))
            {
                await VerifyOwned(old);
                BackupPlan.CheckNoReparse(Path.GetDirectoryName(path)!);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Retention refused a linked archive.");
                File.Delete(path); // One exact managed archive; no recursive directory deletion.
            }
            pruned(old.Command.JobId); removed++;
        }
        return removed;

        string OwnedPath(LocalJob job)
        {
            var path = Path.Combine(plan.Destination, "AutoBack", agentId.ToString("N"), job.Command.JobId.ToString("N"),
                $"backup-{job.Command.CreatedUtc:yyyyMMddTHHmmssZ}." + (job.Report.Encrypted ? "abk" : "zip"));
            if (job.Report.ArchivePath is null || !Path.GetFullPath(job.Report.ArchivePath).Equals(path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Retention refused an archive outside its exact managed job path.");
            BackupPlan.CheckNoReparse(Path.GetDirectoryName(path)!);
            return path;
        }
        async Task VerifyOwned(LocalJob job)
        {
            var path = OwnedPath(job);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Retention refused a linked archive.");
            using var input = ArchiveFile.OpenRead(path, keys);
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            var manifest = await RestoreEngine.ReadManifestAsync(zip, new(), token);
            if (manifest.JobId != job.Command.JobId || manifest.AgentId != agentId) throw new InvalidDataException("Retention archive identity mismatch.");
            await LocalBackupEngine.VerifyAsync(path, manifest, token, keys);
            if (job.Report.ArchiveSha256 is { } expected)
            {
                await using var raw = File.OpenRead(path);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(raw, token)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Retention refused a changed archive.");
            }
        }
    }
}
