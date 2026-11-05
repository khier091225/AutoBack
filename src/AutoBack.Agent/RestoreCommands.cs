using AutoBack.Backup;
using AutoBack.Connection;
using AutoBack.Drive;

namespace AutoBack.Agent;

internal static class RestoreCommands
{
    public static async Task<int> ExecuteAsync(string command, Dictionary<string, string?> options, ProtectedStore store)
    {
        if (command == "restore-status")
        {
            var latest = RestoreHistory.Read(store).Operations.LastOrDefault();
            if (latest is null) { Console.WriteLine("No restore operations recorded."); return 0; }
            Console.WriteLine($"Restore: {latest.Summary.State}\nSource: {latest.SourceDescription}\nDestination: {latest.Summary.Destination}\nFiles: {latest.Summary.Files}\nBytes: {latest.Summary.Bytes}\n{latest.Summary.Detail}");
            return latest.Summary.State == "Completed" ? 0 : 1;
        }
        var archive = options.GetValueOrDefault("--archive");
        var driveFile = options.GetValueOrDefault("--drive-file");
        if (archive is not null && driveFile is not null) throw new ArgumentException("Choose one restore source: --archive or --drive-file.");
        if (command == "verify-restore" && (archive is not null || driveFile is not null)) throw new ArgumentException("verify-restore takes only --destination <restored folder>.");
        if (command == "restore" && archive is null && driveFile is null)
        {
            archive = FolderSelection.ChooseRestoreArchive();
            if (archive is null) { Console.WriteLine("Restore selection cancelled."); return 2; }
        }
        var destination = options.GetValueOrDefault("--destination") ?? FolderSelection.ChooseRestoreDestination(command == "verify-restore");
        if (destination is null) { Console.WriteLine("Restore destination selection cancelled."); return 2; }
        destination = RestoreEngine.NormalizeDestination(destination);
        if (command == "restore" && (Directory.Exists(destination) || File.Exists(destination)))
            throw new IOException("Choose a new restore folder; the destination already exists.");
        if (archive is not null) archive = Path.GetFullPath(archive);
        if (driveFile is not null) driveFile = DriveDownloader.ParseFileId(driveFile);
        var operation = Guid.NewGuid();
        var kind = command == "verify-restore" ? "Verify" : driveFile is not null ? "GoogleDrive" : "Local";
        var description = command == "verify-restore" ? destination : driveFile is not null ? $"https://drive.google.com/file/d/{driveFile}/view" : archive!;
        var summary = new RestoreSummary(operation, command == "verify-restore" ? "Verifying" : driveFile is null ? "Validating" : "Downloading",
            kind, destination, 0, 0, 0, "Starting recovery verification.", DateTimeOffset.UtcNow);
        RestoreHistory.RecoverInterrupted(store);
        RestoreHistory.Save(store, new(summary, description));
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = !cancellation.IsCancellationRequested; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        string? temporaryArchive = null;
        string? lastPrinted = null;
        void Report(string state, int percent, int files, long bytes, string detail)
        {
            summary = summary with { State = state, Percent = percent, Files = files, Bytes = bytes, Detail = Safe(detail), UpdatedUtc = DateTimeOffset.UtcNow };
            RestoreHistory.Save(store, new(summary, description));
            var text = $"{state}: {percent}% | {files} files | {bytes} bytes | {summary.Detail}";
            if (text != lastPrinted) { Console.WriteLine(text); lastPrinted = text; }
        }
        try
        {
            RestoreResult result;
            if (command == "verify-restore") result = await RestoreEngine.VerifyRestoredAsync(destination, cancellation.Token);
            else
            {
                DownloadedBackup? download = null;
                if (driveFile is not null)
                {
                    var parent = Path.GetDirectoryName(destination)!;
                    Directory.CreateDirectory(parent);
                    BackupPlan.CheckNoReparse(parent);
                    temporaryArchive = Path.Combine(parent, ".autoback-download-" + operation.ToString("N") + ".zip");
                    using var http = DriveHttp.CreateClient();
                    download = await new DriveDownloader(store, http).DownloadAsync(driveFile, temporaryArchive,
                        p => Report("Downloading", p.TotalBytes == 0 ? 0 : (int)Math.Min(99, (double)p.Bytes / p.TotalBytes * 100), 0, p.Bytes, p.Detail), cancellation.Token);
                    archive = download.Path;
                }
                result = await new RestoreEngine().RestoreAsync(archive!, destination,
                    new(kind, description, download?.BackupJobId, download?.BackupAgentId),
                    p => { if (p.State != "Completed") Report(p.State, p.Percent, p.Files, p.Bytes, p.Detail); }, cancellation.Token, operationId: operation);
            }
            summary = summary with { State = "Completed", Percent = 100, Files = result.Files, Bytes = result.Bytes,
                BackupJobId = result.BackupJobId, UpdatedUtc = DateTimeOffset.UtcNow,
                Detail = command == "verify-restore" ? "Restored files verified against the manifest and SHA-256 checksums." : "Restore completed; the full restored file set passed SHA-256 verification." };
            RestoreHistory.Save(store, new(summary, description, result));
            Console.WriteLine($"RESTORE VERIFIED\nRecovered files: {result.PayloadDirectory}\nFiles: {result.Files}\nBytes: {result.Bytes}\nBackup job: {result.BackupJobId}\nReport: {Path.Combine(destination, "restore-report.json")}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidDataException or
            DriveException or HttpRequestException or System.Text.Json.JsonException or FormatException or System.Security.Cryptography.CryptographicException or OperationCanceledException)
        {
            var detail = ex is HttpRequestException ? "The cloud download could not complete. Check connectivity and retry with a fresh recovery folder." : Safe(ex.Message);
            summary = summary with { State = ex is OperationCanceledException ? "Cancelled" : "Failed", Detail = detail, UpdatedUtc = DateTimeOffset.UtcNow };
            RestoreHistory.Save(store, new(summary, description));
            Console.Error.WriteLine($"RESTORE {summary.State.ToUpperInvariant()}: {detail}");
            return ex is OperationCanceledException ? 3 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (temporaryArchive is not null && Path.GetDirectoryName(Path.GetFullPath(temporaryArchive)) == Path.GetDirectoryName(destination) &&
                Path.GetFileName(temporaryArchive) == ".autoback-download-" + operation.ToString("N") + ".zip" && File.Exists(temporaryArchive))
            {
                try { File.Delete(temporaryArchive); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string Safe(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).Take(950).ToArray());
}
