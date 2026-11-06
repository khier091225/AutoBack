using AutoBack.Connection;
using System.Text.Json;

namespace AutoBack.Backup;

public sealed record BackupPlan(string Source, string Destination, List<string>? SelectedSources = null, string Target = BackupTargets.LegacyBoth)
{
    public const string MultipleSourcesGuard = "Multiple source folders require AutoBack 0.7 or newer";
    public const string TargetGuard = "Destination modes require AutoBack 0.8 or newer";
    public IReadOnlyList<string> Sources => SelectedSources ?? (IReadOnlyList<string>)[Source];
    public string PlanId => Target != BackupTargets.LegacyBoth ? Protocol.Hash("v3|" + Target + "|" + JsonSerializer.Serialize(Sources.Select(s => s.ToUpperInvariant())) + "|" + Destination.ToUpperInvariant())
        : SelectedSources is null ? Protocol.Hash($"v1|{Source.ToUpperInvariant()}|{Destination.ToUpperInvariant()}")
        : Protocol.Hash("v2|" + JsonSerializer.Serialize(Sources.Select(s => s.ToUpperInvariant())) + "|" + Destination.ToUpperInvariant());
    public BackupConfiguration Summary => new(PlanId, Sources.Count == 1 ? Sources[0] : $"{Sources.Count} selected folders",
        Target == BackupTargets.GoogleDrive ? "Google Drive" : Destination, Sources: Sources.ToList(), Target: Target);
    public static string CacheDirectory(string dataDirectory) => Path.Combine(Path.GetFullPath(dataDirectory), "cloud-cache");

    public static BackupPlan CreateForTarget(IEnumerable<string> sources, string target, string? localDestination, string dataDirectory)
    {
        if (target is not (BackupTargets.GoogleDrive or BackupTargets.LocalOnly)) throw new ArgumentException("Choose Google Drive or Local only.");
        var destination = target == BackupTargets.GoogleDrive ? CacheDirectory(dataDirectory)
            : localDestination ?? throw new ArgumentException("Choose a destination folder for Local only mode.");
        var old = CreateMany(sources, destination);
        var plan = new BackupPlan(TargetGuard, old.Destination, old.Sources.ToList(), target);
        plan.Validate(); return plan;
    }

    public static BackupPlan Create(string source, string destination)
        => CreateMany([source], destination);

    public static BackupPlan CreateMany(IEnumerable<string> sources, string destination)
    {
        static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Length > 1024 || path.Any(char.IsControl))
                throw new ArgumentException("Choose a full local folder path, for example D:\\OfficeFiles.");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
                throw new ArgumentException("This milestone supports local folders, not network shares or device paths.");
            return full;
        }
        var selected = sources.Take(21).Select(Normalize).ToList();
        if (selected.Count is < 1 or > 20) throw new ArgumentException("Choose between 1 and 20 source folders.");
        var plan = selected.Count == 1 ? new BackupPlan(selected[0], Normalize(destination))
            : new BackupPlan(MultipleSourcesGuard, Normalize(destination), selected);
        plan.Validate();
        return plan;
    }

    public void Validate()
    {
        if (!BackupTargets.Valid(Target)) throw new InvalidDataException("Unknown backup destination mode.");
        if (Target != BackupTargets.LegacyBoth && (SelectedSources is null || SelectedSources.Count is < 1 or > 20 || Source != TargetGuard))
            throw new InvalidDataException("Destination modes require AutoBack 0.8 or newer. Configure this client again.");
        if (Target == BackupTargets.LegacyBoth && SelectedSources is not null && (SelectedSources.Count is < 2 or > 20 || Source != MultipleSourcesGuard))
            throw new InvalidDataException("Invalid multi-folder configuration. Use AutoBack 0.7 or newer to configure the client.");
        if (!ValidPath(Destination) || Sources.Any(s => !ValidPath(s)) || !BackupStates.ValidConfiguration(Summary))
            throw new InvalidDataException("The saved backup configuration is invalid. Configure the folders again.");
        for (var i = 0; i < Sources.Count; i++)
        {
            var source = Sources[i];
            if (Contains(source, Destination) || Contains(Destination, source))
                throw new ArgumentException("Backup destination and each source must be separate; neither may contain the other.");
            if (Sources.Take(i).Any(other => Contains(other, source) || Contains(source, other)))
                throw new ArgumentException("Source folders cannot repeat or overlap. A parent folder already includes all of its subfolders.");
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("A selected source folder is missing or inaccessible: " + source);
            CheckNoReparse(source);
        }
        CheckNoReparse(Destination);
    }

    private static bool ValidPath(string path) => BackupStates.ValidText(path, 1024) && Path.IsPathFullyQualified(path) &&
        !path.StartsWith(@"\\", StringComparison.Ordinal) && Path.GetFullPath(path) == path &&
        new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Network;

    public static bool Contains(string parent, string child) => parent.Equals(child, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void CheckNoReparse(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0 && current.Exists)
                throw new IOException("Symbolic links and junctions are not supported in backup paths: " + current.FullName);
        }
    }
}
