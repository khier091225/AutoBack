using AutoBack.Backup;
using AutoBack.Connection;
using AutoBack.Drive;

namespace AutoBack.Agent;

// A fresh identity must not replay reports/uploads belonging to a retired identity.
// The encrypted transition makes the multi-file change restartable under .agent.lock.
public sealed record PairingArchive(Guid AgentId, string Name, string Host, DateTimeOffset RetiredUtc,
    JobJournal Jobs, UploadJournal Uploads, Dictionary<string, DriveFolders>? Folders = null);
internal sealed record PairingTransition(AgentCredentials Credentials, string ArchiveFile, PairingArchive Archive);

internal static class PairingRecovery
{
    internal const string TransitionFile = "pairing-transition.dat";
    internal static PairingArchive Capture(ProtectedStore store, AgentCredentials old)
    {
        var jobs = store.Load<JobJournal>(BackupCoordinator.JournalFile) ?? store.Load<JobJournal>("jobs.dat") ?? new([]);
        var uploads = store.Load<UploadJournal>("drive-uploads.dat") ?? new([]);
        if (jobs.Jobs is null || uploads.Uploads is null) throw new InvalidDataException("Cannot preserve the old pairing history. Check the protected journals before pairing again.");
        return new(old.AgentId, old.Name, old.Host, DateTimeOffset.UtcNow, jobs, uploads,
            store.Load<Dictionary<string, DriveFolders>>("drive-folders.dat"));
    }
    internal static void Replace(ProtectedStore store, AgentCredentials next, PairingArchive archive)
    {
        var file = $"pairing-history-{archive.AgentId:N}-{next.AgentId:N}.dat";
        store.Save(TransitionFile, new PairingTransition(next, file, archive));
        Complete(store);
    }
    internal static void Complete(ProtectedStore store)
    {
        var transition = store.Load<PairingTransition>(TransitionFile);
        if (transition is null) return;
        transition.Credentials.Validate();
        if (transition.Archive is null || transition.Archive.Jobs?.Jobs is null || transition.Archive.Uploads?.Uploads is null ||
            transition.Archive.AgentId == Guid.Empty || transition.Archive.AgentId == transition.Credentials.AgentId ||
            transition.ArchiveFile != $"pairing-history-{transition.Archive.AgentId:N}-{transition.Credentials.AgentId:N}.dat")
            throw new InvalidDataException("Invalid pending pairing recovery. Preserve the protected data directory for inspection.");
        store.Save(transition.ArchiveFile, transition.Archive); // Durable preservation before resetting any active journal.
        store.Save(BackupCoordinator.JournalFile, new JobJournal([]));
        store.Save("jobs.dat", new JobJournal([])); // Do not let legacy fallback rediscover retired work.
        store.Save("drive-uploads.dat", new UploadJournal([]));
        store.Save("drive-folders.dat", new Dictionary<string, DriveFolders>()); // Client folders are bound to the old agent ID.
        store.Save("agent.dat", transition.Credentials);
        store.Delete("pending.dat");
        store.Delete(TransitionFile); // Last: an interrupted change is completed before any worker can start.
    }
}
