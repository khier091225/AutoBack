using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Windows.Forms;
using AutoBack.Agent;
using AutoBack.Backup;
using AutoBack.Connection;
using AutoBack.Drive;
using AutoBack.Drive.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutoBack-folders-tests"));
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AutoBack.slnx"))) repo = repo.Parent;
        try
        {
            var sources = new[] { Path.Combine(root, "user-one", "Documents"), Path.Combine(root, "user-two", "Documents"), Path.Combine(root, "Empty") };
            foreach (var source in sources) Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(sources[0], "report.txt"), "First user's private working folder.");
            File.WriteAllText(Path.Combine(sources[1], "report.txt"), "Second user's separate working folder.");
            Directory.CreateDirectory(Path.Combine(sources[1], "nested", "empty"));
            File.WriteAllText(Path.Combine(sources[1], "nested", "manifest.json"), "Source-owned metadata filename.");
            var destination = Path.Combine(root, "backups");
            Console.WriteLine("CHECK actual folder manager: add/remove, duplicate checks, prefill, save and cancel");
            using (var form = Hidden(new BackupFoldersForm()))
            {
                form.Show(); Application.DoEvents();
                form.LocalOnlyMode.Checked = true;
                Check(!form.SaveButton.Enabled, "empty configuration cannot be saved");
                Check(form.AddFolder(sources[0]) && form.AddFolder(sources[1]), "two independent source folders can be added");
                Check(!form.AddFolder(sources[0]) && !form.AddFolder(Path.GetDirectoryName(sources[0])!), "duplicate and overlapping source picks are rejected");
                form.SourceFolders.SelectedIndex = 1; form.RemoveSelectedFolders();
                Check(form.SourceFolders.Items.Count == 1 && File.Exists(Path.Combine(sources[1], "report.txt")), "Remove changes only the selection, preserving source files");
                form.AddFolder(sources[1]); form.AddFolder(sources[2]); form.DestinationInput.Text = sources[1];
                Check(!form.TryBuildPlan(out _) && !string.IsNullOrWhiteSpace(form.Feedback.Text), "invalid destination explains the problem inside the window");
                form.DestinationInput.Text = destination; Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(repo!.FullName, "docs", "backup-folders-preview.png"));
                form.SaveButton.PerformClick();
                Check(form.DialogResult == DialogResult.OK && form.SelectedPlan!.Sources.SequenceEqual(sources), "Save returns the complete ordered source list");
            }
            var plan = BackupPlan.CreateMany(sources, destination);
            using (var form = Hidden(new BackupFoldersForm(plan)))
            {
                form.Show(); Application.DoEvents();
                Check(form.SourceFolders.Items.Count == 3 && form.DestinationInput.Text == destination, "saved selections and destination reopen prefilled");
                form.SourceFolders.SelectedIndex = 0; form.RemoveSelectedFolders(); ((Button)form.CancelButton!).PerformClick();
                Check(form.SelectedPlan is null && plan.Sources.Count == 3, "Cancel leaves the original configuration unchanged");
            }
            SynchronizationContext.SetSynchronizationContext(null);
            RunAsync(root, repo!, sources, plan).GetAwaiter().GetResult();
            Console.WriteLine("ALL MULTI-FOLDER CHECKS PASSED"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (Directory.Exists(root) && Path.GetDirectoryName(Path.GetFullPath(root)) == parent && Guid.TryParseExact(Path.GetFileName(root), "N", out _)) Directory.Delete(root, true);
        }
    }

    private static BackupFoldersForm Hidden(BackupFoldersForm form)
    { form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-16000, -16000); form.ShowInTaskbar = false; return form; }

    private static async Task RunAsync(string root, DirectoryInfo repo, string[] sources, BackupPlan plan)
    {
        var agentId = Guid.NewGuid();
        Console.WriteLine("CHECK plan persistence, compatibility and source validation");
        var store = new ProtectedStore(Path.Combine(root, "store")); store.Save("backup-plan.dat", plan);
        var loaded = store.Load<BackupPlan>("backup-plan.dat")!; loaded.Validate();
        Check(loaded.Sources.SequenceEqual(sources) && loaded.PlanId == plan.PlanId, "DPAPI roundtrip retains every source and plan identity");
        Check(!Path.IsPathFullyQualified(plan.Source), "legacy agents fail closed instead of silently backing up only one source");
        var legacy = BackupPlan.Create(sources[0], plan.Destination);
        Check(legacy.PlanId == Protocol.Hash($"v1|{legacy.Source.ToUpperInvariant()}|{legacy.Destination.ToUpperInvariant()}"), "one-folder plan IDs remain backward compatible");
        var oldJson = JsonSerializer.Serialize(new { source = sources[0], destination = plan.Destination });
        JsonSerializer.Deserialize<BackupPlan>(oldJson, Protocol.Json)!.Validate();
        Check(BackupStates.ValidConfiguration(plan.Summary) && plan.Summary.Sources!.Count == 3 && BackupPlan.CreateMany(sources.Reverse(), plan.Destination).PlanId != plan.PlanId, "wire summary carries full source list and reorder changes plan identity");
        foreach (var invalid in new[] { new[] { sources[0], sources[0] }, new[] { sources[0], Path.GetDirectoryName(sources[0])! }, Array.Empty<string>(), Enumerable.Repeat(sources[0], 21).ToArray() })
            await Reject(() => { BackupPlan.CreateMany(invalid, plan.Destination); return Task.CompletedTask; });
        await Reject(() => { BackupPlan.CreateMany(sources, Path.Combine(sources[1], "archive")); return Task.CompletedTask; });
        await Reject(() => { BackupPlan.CreateMany([sources[0], Path.Combine(root, "missing")], plan.Destination); return Task.CompletedTask; });

        Console.WriteLine("CHECK all source trees back up and restore without collisions");
        var command = new BackupCommand(Guid.NewGuid(), plan.PlanId, DateTimeOffset.UtcNow);
        var result = await new LocalBackupEngine().CreateAsync(plan, agentId, command, _ => { });
        BackupManifest manifest;
        using (var zip = ZipFile.OpenRead(result.ArchivePath)) manifest = await RestoreEngine.ReadManifestAsync(zip, new());
        Check(manifest.FormatVersion == 2 && manifest.Roots!.Select(r => r.Source).SequenceEqual(sources), "format 2 stores complete source-to-archive mapping");
        var restored = await new RestoreEngine().RestoreAsync(result.ArchivePath, Path.Combine(root, "recovered"), new("Local", "three-folder backup"));
        Check(restored.Files == 3 && restored.Roots!.Count == 3, "all files and selected roots are recovered");
        Check(await File.ReadAllTextAsync(Path.Combine(restored.PayloadDirectory, manifest.Roots![0].Prefix, "report.txt")) == File.ReadAllText(Path.Combine(sources[0], "report.txt")) &&
            await File.ReadAllTextAsync(Path.Combine(restored.PayloadDirectory, manifest.Roots[1].Prefix, "report.txt")) == File.ReadAllText(Path.Combine(sources[1], "report.txt")), "same folder/file names from different locations stay separate");
        Check(Directory.Exists(Path.Combine(restored.PayloadDirectory, manifest.Roots[2].Prefix)) && Directory.Exists(Path.Combine(restored.PayloadDirectory, manifest.Roots[1].Prefix, "nested", "empty")), "empty source roots and nested empty folders survive");
        Check((await RestoreEngine.VerifyRestoredAsync(restored.Destination)).Files == 3, "standalone readback verifies multi-folder recovery");
        await Reject(() => new RestoreEngine().RestoreAsync(result.ArchivePath, Path.Combine(sources[1], "recovery"), new("Local", "must reject second root overlap")));
        var reportPath = Path.Combine(restored.Destination, "restore-report.json");
        var reportText = await File.ReadAllTextAsync(reportPath);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(restored with { Roots = null }, Protocol.Json));
        await Reject(() => RestoreEngine.VerifyRestoredAsync(restored.Destination)); await File.WriteAllTextAsync(reportPath, reportText);
        Directory.Delete(sources[2]);
        await Reject(() => new LocalBackupEngine().CreateAsync(plan, agentId, new(Guid.NewGuid(), plan.PlanId, DateTimeOffset.UtcNow), _ => { }));
        Directory.CreateDirectory(sources[2]);
        Check(Directory.EnumerateFiles(plan.Destination, "*.zip", SearchOption.AllDirectories).Count() == 1, "a missing selected root never creates a partial-success backup");
        foreach (var invalid in new[]
        {
            manifest with { Roots = null }, manifest with { Roots = [manifest.Roots[0], manifest.Roots[0]] },
            manifest with { Roots = [manifest.Roots[0] with { Prefix = "../escape" }, manifest.Roots[1]] },
            manifest with { Roots = [manifest.Roots[0] with { Source = "relative" }, manifest.Roots[1]] },
            manifest with { Files = [manifest.Files[0] with { Path = "undeclared/report.txt" }] },
            manifest with { Files = [manifest.Files[0] with { Path = null! }] }
        }) await Reject(() => { RestoreEngine.ValidateManifest(invalid, new()); return Task.CompletedTask; });
        var single = await new LocalBackupEngine().CreateAsync(legacy, agentId, new(Guid.NewGuid(), legacy.PlanId, DateTimeOffset.UtcNow), _ => { });
        using (var zip = ZipFile.OpenRead(single.ArchivePath)) Check((await RestoreEngine.ReadManifestAsync(zip, new())).FormatVersion == 1, "single-folder archives keep their original format");

        Console.WriteLine("CHECK encrypted multi-folder upload/download, restore and retention");
        var cloudStore = new ProtectedStore(Path.Combine(root, "cloud-store"));
        cloudStore.Save("drive-account.dat", new DriveAccount { ClientId = "test.apps.googleusercontent.com", ClientSecret = "TEST_CLIENT", RefreshToken = "REFRESH_SENTINEL", UserId = "user-one", Email = "test@example.invalid" });
        EncryptionVault.Enable(cloudStore, Path.Combine(root, "recovery.abkey"), "Multi-folder test passphrase 2026");
        var key = EncryptionVault.Load(cloudStore).Active!;
        var encryptedCommand = new BackupCommand(Guid.NewGuid(), plan.PlanId, DateTimeOffset.UtcNow);
        var encrypted = await new LocalBackupEngine().CreateAsync(plan, agentId, encryptedCommand, _ => { }, encryptionKey: key);
        var local = new LocalJob(encryptedCommand, new(encryptedCommand.JobId, 1, "Completed", 100, encrypted.Bytes, encrypted.Bytes, encrypted.Files, encrypted.Files, "Verified", encrypted.ArchivePath, Encrypted: true, ArchiveSha256: encrypted.ArchiveSha256));
        using var google = new FakeGoogle(); using var http = new HttpClient(google);
        await using (var upload = new CloudUploadCoordinator(cloudStore, http, agentId, "Multi-Folder-PC"))
        {
            upload.SetHostAuthorized(true); upload.Tick([local]); await Until(() => upload.NextReport()?.State is "Completed" or "Failed");
            Check(upload.NextReport()?.State == "Completed", "Drive uploader verifies and uploads encrypted format 2");
            var download = await new DriveDownloader(cloudStore, http).DownloadAsync(upload.NextReport()!.FileId!, Path.Combine(root, "download.abk"));
            var cloudRestore = await new RestoreEngine().RestoreAsync(download.Path, Path.Combine(root, "cloud-recovered"), new("GoogleDrive", "test", download.BackupJobId, download.BackupAgentId), keys: EncryptionVault.Load(cloudStore).Resolve);
            Check(cloudRestore.Roots!.Count == 3 && cloudRestore.Files == 3 && (await RestoreEngine.VerifyRestoredAsync(cloudRestore.Destination)).Files == 3, "cloud recovery retains and verifies every selected source");
        }
        var old = new LocalJob(command, new(command.JobId, 1, "Completed", 100, result.Bytes, result.Bytes, result.Files, result.Files, "Verified", result.ArchivePath, ArchiveSha256: result.ArchiveSha256));
        Check(await LocalRetention.PruneAsync(plan, agentId, [old, local], 1, new HashSet<Guid>(), EncryptionVault.Load(cloudStore).Resolve, _ => { }) == 1 && File.Exists(encrypted.ArchivePath), "retention recognizes multi-folder versions and protects the newer encrypted copy");

        Console.WriteLine("CHECK actual client CLI and host-triggered multi-folder backup");
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var agent = Path.Combine(repo.FullName, "src", "AutoBack.Agent", "bin", config, "net10.0-windows", "AutoBack.Agent.exe");
        var dataDirectory = Path.Combine(root, "process-store"); var processStore = new ProtectedStore(dataDirectory);
        var arguments = new List<string> { "configure" }; foreach (var source in sources) { arguments.Add("--source"); arguments.Add(source); }
        arguments.AddRange(["--destination", Path.Combine(root, "process-backups")]);
        Check(await Cli(arguments.ToArray()) == 0 && processStore.Load<BackupPlan>("backup-plan.dat")!.Sources.Count == 3, "repeated --source flags persist all selected folders");
        var unchanged = processStore.Load<BackupPlan>("backup-plan.dat")!.PlanId;
        Check(await Cli(["configure", "--source", sources[0], "--source", sources[0], "--destination", plan.Destination]) != 0 && processStore.Load<BackupPlan>("backup-plan.dat")!.PlanId == unchanged, "invalid CLI configuration preserves the last valid folder list");
        await using var host = new HostServer(Path.Combine(root, "process-host"), 0, IPAddress.Loopback); host.Start();
        var invite = host.CreateInvitation("127.0.0.1"); var identity = AgentClient.CreateCredentials(invite, "Folders-PC"); await new AgentClient().PairAsync(invite, identity); processStore.Save("agent.dat", identity);
        using var worker = Start(["run"]); var output = worker.StandardOutput.ReadToEndAsync(); var error = worker.StandardError.ReadToEndAsync();
        try
        {
            await Until(() => host.GetSnapshots().Single().Configuration?.Sources?.Count == 3);
            var queued = host.QueueBackup(identity.AgentId); await Until(() => host.GetJobs().Single(j => j.Command.JobId == queued.Command.JobId).Report.State is "Completed" or "Failed");
            var job = host.GetJobs().Single();
            Check(job.Report.State == "Completed" && job.Report.TotalFiles == 3 && host.GetSnapshots().Single().Configuration!.Sources!.SequenceEqual(sources), "real agent reports every source and completes the host-triggered backup");
            var recovered = await new RestoreEngine().RestoreAsync(job.Report.ArchivePath!, Path.Combine(root, "process-recovered"), new("Local", "actual client"));
            Check(recovered.Roots!.Count == 3 && recovered.Files == 3, "actual client archive restores all three source roots");
        }
        finally { if (!worker.HasExited) worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); await output; await error; }
        var evidence = Path.Combine(repo.FullName, "artifacts", "multi-folder-validation"); Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new { verifiedAtUtc = DateTimeOffset.UtcNow, sourceFolders = 3, duplicateLeafNames = true,
            filesRecovered = 3, missingRootFails = true, legacySingleFolderCompatible = true, encryptedCloudRoundtrip = "Simulated Google HTTP passed", actualClientAndHost = "Passed", folderManagerUi = "Passed" }, Protocol.Json));

        Process Start(string[] args)
        {
            var start = new ProcessStartInfo(agent) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in args) start.ArgumentList.Add(argument); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(dataDirectory); return Process.Start(start)!;
        }
        async Task<int> Cli(string[] args)
        {
            using var process = Start(args); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(entireProcessTree: true); throw; }
            await stdout; await stderr; return process.ExitCode;
        }
    }
    private static void Check(bool okay, string detail) { if (!okay) throw new InvalidOperationException("FAIL: " + detail); Console.WriteLine("  PASS " + detail); }
    private static async Task Until(Func<bool> ready)
    { var watch = Stopwatch.StartNew(); while (!ready()) { if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Multi-folder operation timed out."); await Task.Delay(50); } }
    private static async Task Reject(Func<Task> run)
    {
        try { await run(); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected folder validation failure did not occur.");
    }
}
