using AutoBack.Backup;
using AutoBack.Connection;
using AutoBack.Drive;

namespace AutoBack.Agent;

internal sealed class AgentMaintenance(ProtectedStore store, Guid agentId, BackupCoordinator backups, CloudUploadCoordinator uploads, HttpClient http) : IAsyncDisposable
{
    private Task? _task;
    private readonly CancellationTokenSource _lifetime = new();
    private string? _lastSignature;
    private DateTimeOffset _next;
    private AgentProtectionSummary? _summary;
    public bool IsBusy => _task is { IsCompleted: false };
    public bool Faulted => _task?.IsFaulted == true;
    public AgentProtectionSummary Summary => _summary ?? new(EncryptionVault.Load(store).Enabled, RetentionPolicy.Load(store).KeepLocal,
        RetentionPolicy.Load(store).KeepCloud, "Ready", "Retention is checked when the client is idle and authorized by the host.");

    public void Tick()
    {
        if (IsBusy || backups.IsBusy || uploads.IsBusy) return;
        var policy = RetentionPolicy.Load(store);
        var jobs = backups.AllJobs(); var cloud = uploads.AllUploads();
        var plan = backups.LoadPlan();
        var signature = plan?.PlanId + ";" + string.Join("|", jobs.Select(j => $"{j.Command.JobId}/{j.Report.State}/{j.Report.ArchivePruned}/{j.Acknowledged}")) + ";" +
            string.Join("|", cloud.Select(c => $"{c.JobId}/{c.Report.State}/{c.Report.Trashed}/{c.Acknowledged}"));
        if (_lastSignature == signature && DateTimeOffset.UtcNow < _next) return;
        _lastSignature = signature; _next = DateTimeOffset.UtcNow.AddHours(1);
        _task = Task.Run(async () =>
        {
            var vault = EncryptionVault.Load(store);
            var localCount = 0; var cloudCount = 0; var temporaryCount = 0;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                if (plan?.Target != BackupTargets.LocalOnly)
                    temporaryCount = await new CloudCacheCleanup(store, http, agentId).RunAsync(jobs, cloud, backups.MarkTemporaryRemoved, timeout.Token);
                var protectedJobs = jobs.Where(j => j.Target != BackupTargets.LocalOnly && (cloud.Any(c => c.JobId == j.Command.JobId && c.Report.State != "Completed") ||
                        (uploads.Configuration.State != "Not connected" && !cloud.Any(c => c.JobId == j.Command.JobId && c.Report.State == "Completed"))))
                    .Select(j => j.Command.JobId).ToHashSet();
                if (plan is not null && plan.Target != BackupTargets.GoogleDrive && policy.KeepLocal > 0)
                    localCount = await LocalRetention.PruneAsync(plan, agentId, jobs, policy.KeepLocal, protectedJobs, vault.Resolve, backups.MarkPruned, timeout.Token);
                if (policy.KeepCloud > 0 && plan?.Target != BackupTargets.LocalOnly)
                    cloudCount = await new DriveRetention(store, http, agentId).PruneAsync(cloud, policy.KeepCloud, uploads.MarkTrashed, timeout.Token);
                _summary = new(vault.Enabled, policy.KeepLocal, policy.KeepCloud, "Ready",
                    $"Cleanup: {temporaryCount} verified temporary archive(s) removed; {localCount} local version(s) pruned; {cloudCount} cloud version(s) trashed.", DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Cryptography.CryptographicException or
                DriveException or HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException)
            {
                _summary = new(vault.Enabled, policy.KeepLocal, policy.KeepCloud, "Attention", "Retention paused: " + DriveHttp.SafeMessage(ex is HttpRequestException ? "Cloud request failed." : ex.Message), DateTimeOffset.UtcNow);
            }
            store.Save("protection-status.dat", _summary);
        });
    }
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync(); if (_task is not null) await _task; _lifetime.Dispose();
    }
}
