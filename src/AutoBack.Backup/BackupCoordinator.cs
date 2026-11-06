using AutoBack.Connection;

namespace AutoBack.Backup;

public sealed record LocalJob(BackupCommand Command, BackupReport Report, bool Acknowledged = false,
    string Target = BackupTargets.LegacyBoth, string? CacheRoot = null, string? CloudAccountKey = null);
public sealed record JobJournal(List<LocalJob> Jobs);

/// <summary>Runs one backup at a time while transport polling continues independently.</summary>
public sealed class BackupCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ProtectedStore _store;
    private readonly Guid _agentId;
    private JobJournal _journal;
    private Task? _task;
    private CancellationTokenSource? _cancellation;
    private bool _stopping;
    private readonly Func<string?>? _cloudAccount;
    public const string JournalFile = "jobs-v2.dat";

    public BackupCoordinator(ProtectedStore store, Guid agentId, Func<string?>? cloudAccount = null)
    {
        _store = store;
        _agentId = agentId;
        _cloudAccount = cloudAccount;
        _journal = store.Load<JobJournal>(JournalFile) ?? store.Load<JobJournal>("jobs.dat") ?? new([]);
        if (_journal.Jobs is null) throw new InvalidDataException("The local job journal is invalid.");
        var recovered = _journal.Jobs.Select(j => BackupStates.Terminal(j.Report.State) ? j : j with
        {
            Report = j.Report with { Sequence = j.Report.Sequence + 1, State = "Failed", Detail = "Backup interrupted by an agent restart. Start a new job; no completion was confirmed." },
            Acknowledged = false
        }).ToList();
        Save(new(recovered));
    }

    public BackupPlan? LoadPlan() => _store.Load<BackupPlan>("backup-plan.dat");
    public bool IsBusy { get { lock (_gate) return _task is { IsCompleted: false }; } }
    public bool Faulted { get { lock (_gate) return _task?.IsFaulted == true; } }
    public IReadOnlyList<LocalJob> AllJobs() { lock (_gate) return _journal.Jobs.ToArray(); }
    public void MarkPruned(Guid jobId) => Update(jobId, old => old with { ArchivePruned = true });
    public IReadOnlyList<LocalJob> CompletedJobs()
    {
        lock (_gate) return _journal.Jobs.Where(j => j.Report.State == "Completed" && !j.Report.ArchivePruned).ToArray();
    }
    public BackupReport? NextReport()
    {
        lock (_gate) return _journal.Jobs.FirstOrDefault(j => !j.Acknowledged)?.Report;
    }

    public void Acknowledge(Guid? jobId, long sequence)
    {
        if (jobId is null) return;
        lock (_gate)
        {
            var job = _journal.Jobs.SingleOrDefault(j => j.Command.JobId == jobId);
            if (job is null || job.Acknowledged || sequence < job.Report.Sequence || !BackupStates.Terminal(job.Report.State)) return;
            Save(new(_journal.Jobs.Select(j => j == job ? j with { Acknowledged = true } : j).ToList()));
        }
    }

    public void Handle(BackupCommand? command)
    {
        if (command is null) return;
        lock (_gate)
        {
            if (_stopping) return;
            var existing = _journal.Jobs.SingleOrDefault(j => j.Command.JobId == command.JobId);
            if (existing is not null)
            {
                if (existing.Acknowledged)
                    Save(new(_journal.Jobs.Select(j => j == existing ? j with { Acknowledged = false } : j).ToList()));
                if (command.CancelRequested && !BackupStates.Terminal(existing.Report.State)) _cancellation?.Cancel();
                return;
            }
            if (_task is { IsCompleted: false }) return;
            var report = new BackupReport(command.JobId, 1, command.CancelRequested ? "Cancelled" : "Preparing", 0, 0, 0, 0, 0,
                command.CancelRequested ? "Cancelled before the backup started." : "Client accepted the backup request.");
            Save(new([.. _journal.Jobs, new LocalJob(command, report)]));
            if (command.CancelRequested) return;
            _cancellation?.Dispose();
            _cancellation = new();
            var token = _cancellation.Token;
            _task = Task.Run(() => ExecuteAsync(command, token));
        }
    }

    public void CancelCurrent()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    private async Task ExecuteAsync(BackupCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var plan = LoadPlan() ?? throw new InvalidOperationException("Source and destination folders are not configured on this client.");
            if (plan.PlanId != command.PlanId) throw new InvalidOperationException("Folder or destination settings changed after this backup was queued. Request a new job.");
            CapturePlan(command.JobId, plan, null);
            string? accountKey = null;
            if (plan.Target == BackupTargets.GoogleDrive)
            {
                if (plan.Destination != BackupPlan.CacheDirectory(_store.DirectoryPath)) throw new InvalidDataException("Google Drive staging must use this agent's managed cache.");
                accountKey = _cloudAccount?.Invoke();
                if (!Protocol.ValidSecret(accountKey)) throw new InvalidOperationException("Connect Google Drive on this client before starting a cloud backup. No temporary archive was created.");
            }
            if (accountKey is not null) CapturePlan(command.JobId, plan, accountKey);
            var key = EncryptionVault.Load(_store).Active;
            if (command.Encrypt is { } expected && expected != (key is not null)) throw new InvalidOperationException("Encryption settings changed after the backup was queued. Request a new job.");
            var result = await new LocalBackupEngine().CreateAsync(plan, _agentId, command,
                progress => Update(command.JobId, old => old with { State = progress.State, Percent = progress.Percent,
                    BytesCopied = progress.BytesCopied, TotalBytes = progress.TotalBytes, FilesCopied = progress.FilesCopied,
                    TotalFiles = progress.TotalFiles, Detail = progress.Detail }), cancellationToken, key);
            Update(command.JobId, old => old with { State = "Completed", Percent = 100, BytesCopied = result.Bytes,
                TotalBytes = result.Bytes, FilesCopied = result.Files, TotalFiles = result.Files, ArchivePath = result.ArchivePath,
                Encrypted = result.Encrypted, ArchiveSha256 = result.ArchiveSha256,
                Detail = plan.Target == BackupTargets.GoogleDrive ? "Temporary archive prepared and verified; waiting for confirmed Google Drive upload."
                    : result.Encrypted ? "Encrypted backup completed; authenticated decryption and SHA-256 verification passed." : "Local backup completed; the saved ZIP passed SHA-256 verification." });
        }
        catch (OperationCanceledException)
        {
            Update(command.JobId, old => old with { State = "Cancelled", Detail = "Backup cancelled before a complete archive was published." });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            var message = new string(ex.Message.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            if (message.Length > 1000) message = message[..1000];
            Update(command.JobId, old => old with { State = "Failed", Detail = message, Retryable = ex is IOException });
        }
    }

    private void Update(Guid id, Func<BackupReport, BackupReport> update)
    {
        lock (_gate)
        {
            var job = _journal.Jobs.Single(j => j.Command.JobId == id);
            var report = update(job.Report) with { Sequence = job.Report.Sequence + 1 };
            Save(new(_journal.Jobs.Select(j => j == job ? j with { Report = report, Acknowledged = false } : j).ToList()));
        }
    }

    private void Save(JobJournal journal)
    {
        _store.Save(JournalFile, journal);
        _journal = journal;
    }

    private void CapturePlan(Guid jobId, BackupPlan plan, string? accountKey)
    {
        lock (_gate)
        {
            Save(new(_journal.Jobs.Select(j => j.Command.JobId == jobId ? j with
            { Target = plan.Target, CacheRoot = plan.Target == BackupTargets.GoogleDrive ? plan.Destination : null, CloudAccountKey = accountKey,
                Report = j.Report with { Target = plan.Target, Sequence = j.Report.Sequence + 1 } } : j).ToList()));
        }
    }

    public void MarkTemporaryRemoved(Guid id) => Update(id, old => old with { ArchivePruned = true, TemporaryArchiveRemoved = true });

    public async ValueTask DisposeAsync()
    {
        Task? task;
        lock (_gate) { _stopping = true; _cancellation?.Cancel(); task = _task; }
        if (task is not null) await task;
        _cancellation?.Dispose();
    }
}
