using AutoBack.Connection;

namespace AutoBack.Backup;

public sealed record RestoreHistoryEntry(RestoreSummary Summary, string SourceDescription, RestoreResult? Result = null);
public sealed record RestoreJournal(List<RestoreHistoryEntry> Operations);

public static class RestoreHistory
{
    public static RestoreSummary? Latest(ProtectedStore store) => Read(store).Operations.LastOrDefault()?.Summary;
    public static RestoreJournal Read(ProtectedStore store)
    {
        var journal = store.Load<RestoreJournal>("restores.dat") ?? new([]);
        if (journal.Operations is null || journal.Operations.Any(o => o is null || o.Summary is null || !RestoreStates.ValidSummary(o.Summary)))
            throw new InvalidDataException("The saved restore history is invalid.");
        return journal;
    }

    public static void Save(ProtectedStore store, RestoreHistoryEntry entry)
    {
        if (!RestoreStates.ValidSummary(entry.Summary)) throw new InvalidDataException("Invalid restore summary.");
        var operations = Read(store).Operations;
        var existing = operations.FindIndex(o => o.Summary.OperationId == entry.Summary.OperationId);
        if (existing >= 0) operations[existing] = entry;
        else operations.Add(entry);
        store.Save("restores.dat", new RestoreJournal(operations.TakeLast(100).ToList()));
    }

    public static void RecoverInterrupted(ProtectedStore store)
    {
        var journal = Read(store);
        if (journal.Operations.All(o => RestoreStates.Terminal(o.Summary.State))) return;
        store.Save("restores.dat", new RestoreJournal(journal.Operations.Select(o => RestoreStates.Terminal(o.Summary.State) ? o : o with
        {
            Summary = o.Summary with { State = "Interrupted", Detail = "Restore interrupted before completion was confirmed. Select a fresh folder and retry.", UpdatedUtc = DateTimeOffset.UtcNow }
        }).ToList()));
    }
}
