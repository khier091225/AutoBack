namespace AutoBack.Connection;

public static class BackupTargets
{
    public const string GoogleDrive = "GoogleDrive";
    public const string LocalOnly = "LocalOnly";
    public const string LegacyBoth = "LegacyBoth";
    public static bool Valid(string target) => target is GoogleDrive or LocalOnly or LegacyBoth;
    public static string Label(string target) => target switch
    { GoogleDrive => "Google Drive", LocalOnly => "Local only", _ => "Local + Google Drive (existing setup)" };
}
