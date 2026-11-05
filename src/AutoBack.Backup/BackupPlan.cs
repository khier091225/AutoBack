using AutoBack.Connection;

namespace AutoBack.Backup;

public sealed record BackupPlan(string Source, string Destination)
{
    public string PlanId => Protocol.Hash($"v1|{Source.ToUpperInvariant()}|{Destination.ToUpperInvariant()}");
    public BackupConfiguration Summary => new(PlanId, Source, Destination);

    public static BackupPlan Create(string source, string destination)
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
        var plan = new BackupPlan(Normalize(source), Normalize(destination));
        if (Contains(plan.Source, plan.Destination) || Contains(plan.Destination, plan.Source))
            throw new ArgumentException("Source and destination must be separate folders; neither may contain the other.");
        plan.Validate();
        return plan;
    }

    public void Validate()
    {
        if (!BackupStates.ValidText(Source, 1024) || !BackupStates.ValidText(Destination, 1024) ||
            !Path.IsPathFullyQualified(Source) || !Path.IsPathFullyQualified(Destination) ||
            Source.StartsWith(@"\\", StringComparison.Ordinal) || Destination.StartsWith(@"\\", StringComparison.Ordinal) ||
            Path.GetFullPath(Source) != Source || Path.GetFullPath(Destination) != Destination ||
            Contains(Source, Destination) || Contains(Destination, Source))
            throw new InvalidDataException("The saved backup configuration is invalid. Configure the folders again.");
        if (!Directory.Exists(Source)) throw new DirectoryNotFoundException("The configured source folder does not exist or is inaccessible: " + Source);
        CheckNoReparse(Source);
        CheckNoReparse(Destination);
    }

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
