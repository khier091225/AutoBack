using System.Text.Json;
using AutoBack.Backup;
using AutoBack.Connection;

namespace AutoBack.Drive;

public sealed record SavedUpload(Guid JobId, string ArchivePath, string AccountKey, UploadCheckpoint Checkpoint,
    CloudReport Report, DateTimeOffset NextAttemptUtc, int Attempts = 0, bool Acknowledged = false, Guid GrantGeneration = default);
public sealed record UploadJournal(List<SavedUpload> Uploads);

public sealed class CloudUploadCoordinator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ProtectedStore _store;
    private readonly GoogleTokenProvider _tokens;
    private readonly GoogleAuthorization _authorization;
    private readonly DriveUploader _uploader;
    private UploadJournal _journal;
    private Task? _task;
    private CancellationTokenSource? _cancellation;
    private bool _stopping;
    private bool _hostAuthorized;

    public CloudUploadCoordinator(ProtectedStore store, HttpClient http, Guid agentId, string agentName)
    {
        _store = store;
        _tokens = new(store, http);
        _authorization = new(store, http);
        _uploader = new(store, _tokens, http, agentId, agentName);
        _journal = store.Load<UploadJournal>("drive-uploads.dat") ?? new([]);
        if (_journal.Uploads is null) throw new InvalidDataException("The cloud upload journal is invalid.");
        Save(new(_journal.Uploads.Select(u => u.Report.State == "Uploading" ? u with
        {
            Report = u.Report with { Sequence = u.Report.Sequence + 1, State = "Retrying", Detail = "Resuming a saved upload after agent restart." },
            NextAttemptUtc = DateTimeOffset.UtcNow, Acknowledged = false
        } : u).ToList()));
    }

    public CloudConfiguration Configuration => _authorization.Status();
    public bool Faulted { get { lock (_gate) return _task?.IsFaulted == true; } }
    public bool IsBusy { get { lock (_gate) return _task is { IsCompleted: false }; } }
    public IReadOnlyList<SavedUpload> AllUploads() { lock (_gate) return _journal.Uploads.ToArray(); }
    public void MarkTrashed(Guid id) => Update(id, old => old with { Report = old.Report with { Trashed = true } });
    public CloudReport? NextReport() { lock (_gate) return _journal.Uploads.FirstOrDefault(u => !u.Acknowledged)?.Report; }

    public void Acknowledge(Guid? jobId, long sequence)
    {
        if (jobId is null) return;
        lock (_gate)
        {
            var upload = _journal.Uploads.SingleOrDefault(u => u.JobId == jobId);
            if (upload is null || upload.Acknowledged || sequence < upload.Report.Sequence) return;
            Save(new(_journal.Uploads.Select(u => u == upload ? u with { Acknowledged = true } : u).ToList()));
        }
    }

    public void SetHostAuthorized(bool authorized)
    {
        lock (_gate)
        {
            _hostAuthorized = authorized;
            if (!authorized) _cancellation?.Cancel();
        }
    }

    public void Tick(IReadOnlyList<LocalJob> completed)
    {
        lock (_gate)
        {
            if (_stopping || !_hostAuthorized) return;
            var account = _tokens.Account;
            if (account is null) return;
            var additions = completed.Where(j => j.Report.ArchivePath is not null && _journal.Uploads.All(u => u.JobId != j.Command.JobId))
                .Select(j => new SavedUpload(j.Command.JobId, j.Report.ArchivePath!, account.Key, new(),
                    new(j.Command.JobId, 1, "Queued", 0, 0, 0, "Local backup is complete; waiting for direct Google Drive upload."), DateTimeOffset.UtcNow,
                    GrantGeneration: account.Generation)).ToArray();
            if (additions.Length > 0) Save(new([.. _journal.Uploads, .. additions]));
            if (_task is { IsCompleted: false }) return;
            foreach (var other in _journal.Uploads.Where(u => u.AccountKey != account.Key && u.Report.State is "Queued" or "Retrying").ToArray())
                Update(other.JobId, u => u with { Report = u.Report with { State = "Needs sign-in", Detail = "This backup is bound to another Google account. Reconnect its original account to resume." } });
            var next = _journal.Uploads.FirstOrDefault(u => u.AccountKey == account.Key &&
                (u.Report.State is "Queued" or "Retrying" || (u.Report.State == "Needs sign-in" && u.GrantGeneration != account.Generation)) && u.NextAttemptUtc <= DateTimeOffset.UtcNow);
            if (next is null) return;
            if (account.NeedsSignIn || account.Key != next.AccountKey)
            {
                if (next.Report.State != "Needs sign-in")
                    Update(next.JobId, u => u with { Report = u.Report with { State = "Needs sign-in", Detail = "Sign in to the original Google account on this client to upload this backup." } });
                return;
            }
            _cancellation?.Dispose();
            _cancellation = new();
            var token = _cancellation.Token;
            Update(next.JobId, u => u with { GrantGeneration = account.Generation });
            _task = Task.Run(() => UploadAsync(next, token));
        }
    }

    private async Task UploadAsync(SavedUpload upload, CancellationToken token)
    {
        try
        {
            Update(upload.JobId, u => u with { Report = u.Report with { State = "Uploading", Detail = "Checking the local archive and resuming Google Drive upload." } });
            var result = await _uploader.UploadAsync(upload.JobId, upload.ArchivePath, upload.AccountKey, upload.Checkpoint,
                checkpoint => Update(upload.JobId, u => u with { Checkpoint = checkpoint }),
                progress => Update(upload.JobId, u => u with { Report = u.Report with
                {
                    State = "Uploading", BytesUploaded = progress.ConfirmedBytes, TotalBytes = progress.TotalBytes,
                    Percent = progress.TotalBytes == 0 ? 0 : (int)Math.Min(99, (double)progress.ConfirmedBytes / progress.TotalBytes * 100), Detail = progress.Detail
                } }), token);
            Update(upload.JobId, u => u with { Report = u.Report with
            {
                State = "Completed", Percent = 100, BytesUploaded = result.Size, TotalBytes = result.Size,
                FileId = result.FileId, WebLink = result.WebLink, Detail = "Cloud upload completed; Google Drive confirmed the file size and checksum."
            } });
        }
        catch (Exception ex) when (ex is DriveException or HttpRequestException or OperationCanceledException or IOException or
            UnauthorizedAccessException or InvalidDataException or JsonException or KeyNotFoundException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            var kind = ex is DriveException drive ? drive.Failure : ex is HttpRequestException or OperationCanceledException ? DriveFailure.Retryable : DriveFailure.Permanent;
            var detail = ex is DriveException known ? known.Message : kind == DriveFailure.Retryable
                ? "Upload paused or network unavailable. The local backup and resumable session are saved."
                : "The local archive or cloud response could not be read or verified. The local backup result is preserved; inspect the file and use drive-retry.";
            Update(upload.JobId, u => u with
            {
                Report = u.Report with { State = kind == DriveFailure.Authorization ? "Needs sign-in" : kind == DriveFailure.Retryable ? "Retrying" : "Failed", Detail = DriveHttp.SafeMessage(detail) },
                Attempts = Math.Min(10, u.Attempts + 1),
                NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(8, u.Attempts + 1))) + Random.Shared.NextDouble())
            });
        }
    }

    private void Update(Guid id, Func<SavedUpload, SavedUpload> update)
    {
        lock (_gate)
        {
            var old = _journal.Uploads.Single(u => u.JobId == id);
            var next = update(old);
            next = next with { Report = next.Report with { Sequence = old.Report.Sequence + 1 }, Acknowledged = false };
            Save(new(_journal.Uploads.Select(u => u == old ? next : u).ToList()));
        }
    }

    private void Save(UploadJournal journal) { _store.Save("drive-uploads.dat", journal); _journal = journal; }

    public static int RetrySaved(ProtectedStore store)
    {
        var journal = store.Load<UploadJournal>("drive-uploads.dat");
        if (journal is null) return 0;
        var count = journal.Uploads.Count(u => u.Report.State is "Failed" or "Needs sign-in" or "Retrying");
        store.Save("drive-uploads.dat", new UploadJournal(journal.Uploads.Select(u => u.Report.State is "Failed" or "Needs sign-in" or "Retrying" ? u with
        {
            Report = u.Report with { Sequence = u.Report.Sequence + 1, State = "Queued", Detail = "Cloud retry requested on the client; the existing local backup will be reused." },
            NextAttemptUtc = DateTimeOffset.UtcNow, Attempts = 0, Acknowledged = false
        } : u).ToList()));
        return count;
    }

    public async ValueTask DisposeAsync()
    {
        Task? task;
        lock (_gate) { if (_stopping) return; _stopping = true; _cancellation?.Cancel(); task = _task; }
        if (task is not null) await task;
        _cancellation?.Dispose();
    }
}
