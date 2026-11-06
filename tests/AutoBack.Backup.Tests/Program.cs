using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBack.Backup;
using AutoBack.Connection;

var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutoBack-backup-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
try
{
    await Run("archives preserve content, nested/empty folders, versions, and checksums", async () =>
    {
        var plan = Plan("archive");
        Directory.CreateDirectory(Path.Combine(plan.Source, "nested", "empty"));
        await File.WriteAllTextAsync(Path.Combine(plan.Source, "manifest.json"), "source file with a reserved-looking name");
        await File.WriteAllTextAsync(Path.Combine(plan.Source, "nested", "notes-ñ.txt"), "Accounting data ✓\nsecond line");
        var binary = RandomNumberGenerator.GetBytes(400_000);
        await File.WriteAllBytesAsync(Path.Combine(plan.Source, "binary.bin"), binary);
        File.Create(Path.Combine(plan.Source, "zero.txt")).Dispose();
        var states = new List<string>();
        var first = await new LocalBackupEngine().CreateAsync(plan, Guid.NewGuid(), Command(plan), p => states.Add(p.State));
        Check(File.Exists(first.ArchivePath) && first.Files == 4, "full backup archive published");
        using (var zip = ZipFile.OpenRead(first.ArchivePath))
        {
            Check(zip.GetEntry("files/nested/empty/") is not null, "empty directory is preserved");
            using var data = new MemoryStream();
            await zip.GetEntry("files/binary.bin")!.Open().CopyToAsync(data);
            Check(data.ToArray().SequenceEqual(binary), "binary contents match the source");
            var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(zip.GetEntry("manifest.json")!.Open(), Protocol.Json);
            Check(manifest!.Files.Count == 4 && manifest.Files.All(f => f.Sha256.Length == 64), "per-file SHA-256 manifest present");
            Check(zip.GetEntry("files/manifest.json") is not null, "source files cannot collide with backup metadata");
        }
        Check(states.Contains("Copying") && states.Contains("Verifying"), "real copying and verification progress emitted");
        var firstHash = await HashFile(first.ArchivePath);
        await File.WriteAllTextAsync(Path.Combine(plan.Source, "manifest.json"), "new version");
        var second = await new LocalBackupEngine().CreateAsync(plan, Guid.NewGuid(), Command(plan), _ => { });
        Check(second.ArchivePath != first.ArchivePath && File.Exists(first.ArchivePath), "new version preserves the previous archive");
        var currentHash = await HashFile(first.ArchivePath);
        Check(firstHash.SequenceEqual(currentHash), "previous version remains unchanged");
        BackupManifest expected;
        using (var readZip = ZipFile.OpenRead(second.ArchivePath))
            expected = (await JsonSerializer.DeserializeAsync<BackupManifest>(readZip.GetEntry("manifest.json")!.Open(), Protocol.Json))!;
        using (var changedZip = ZipFile.Open(second.ArchivePath, ZipArchiveMode.Update))
        {
            changedZip.GetEntry("files/binary.bin")!.Delete();
            binary[0] ^= 0xFF;
            using var changedData = changedZip.CreateEntry("files/binary.bin").Open();
            changedData.Write(binary);
        }
        await RejectAsync(() => LocalBackupEngine.VerifyAsync(second.ArchivePath, expected));
        Console.WriteLine("  PASS checksum verification rejects modified archive content");
        var empty = Plan("empty");
        var emptyResult = await new LocalBackupEngine().CreateAsync(empty, Guid.NewGuid(), Command(empty), _ => { });
        Check(emptyResult.Files == 0 && emptyResult.Bytes == 0 && File.Exists(emptyResult.ArchivePath), "empty source can be backed up");
    });

    await Run("invalid plans, locked/changed files, cancellation, and partial cleanup", async () =>
    {
        var plan = Plan("failure");
        await File.WriteAllTextAsync(Path.Combine(plan.Source, "work.txt"), "original data");
        Reject(() => BackupPlan.Create(plan.Source, Path.Combine(plan.Source, "backups")));
        Reject(() => BackupPlan.Create(plan.Source, Path.GetDirectoryName(plan.Source)!));
        Reject(() => BackupPlan.Create(plan.Source, plan.Source));
        Reject(() => BackupPlan.Create("relative", plan.Destination));
        Check(BackupPlan.Contains(Path.GetPathRoot(plan.Source)!, plan.Source), "drive-root containment is checked correctly");
        using (var locked = new FileStream(Path.Combine(plan.Source, "work.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await RejectAsync(() => new LocalBackupEngine().CreateAsync(plan, Guid.NewGuid(), Command(plan), _ => { }));
        Check(!Directory.EnumerateFiles(plan.Destination, "*.zip", SearchOption.AllDirectories).Any(), "locked file does not produce a successful archive");
        await RejectAsync(() => new LocalBackupEngine().CreateAsync(plan, Guid.NewGuid(), Command(plan), p =>
        {
            if (p.State == "Copying") File.AppendAllText(Path.Combine(plan.Source, "work.txt"), " changed");
        }));
        Check(!Directory.EnumerateFiles(plan.Destination, "*.partial", SearchOption.AllDirectories).Any(), "failed copy cleans its own partial file");
        using var cancellation = new CancellationTokenSource();
        await RejectAsync(() => new LocalBackupEngine().CreateAsync(plan, Guid.NewGuid(), Command(plan), p =>
        {
            if (p.State == "Verifying") cancellation.Cancel();
        }, cancellation.Token));
        Check(!Directory.EnumerateFiles(plan.Destination, "*.zip", SearchOption.AllDirectories).Any(), "cancelled verification never publishes a complete ZIP");
        Check(!Directory.EnumerateFiles(plan.Destination, "*.partial", SearchOption.AllDirectories).Any(), "cancelled backup cleans its partial file");
        Check(File.ReadAllText(Path.Combine(plan.Source, "work.txt")).StartsWith("original data"), "source data is preserved");
    });

    await Run("persistent journal, replay, interrupted recovery, and cancelled queued job", async () =>
    {
        var plan = Plan("journal");
        await File.WriteAllTextAsync(Path.Combine(plan.Source, "data.txt"), "journal test");
        var store = new ProtectedStore(Path.Combine(root, "journal-store"));
        store.Save("backup-plan.dat", plan);
        var command = Command(plan);
        var agentId = Guid.NewGuid();
        await using (var coordinator = new BackupCoordinator(store, agentId))
        {
            coordinator.Handle(command);
            coordinator.Handle(command);
            await Until(() => coordinator.NextReport()?.State == "Completed", TimeSpan.FromSeconds(10));
            var report = coordinator.NextReport()!;
            Check(report.Percent == 100 && report.ArchivePath is not null, "completion reported only after verification");
            coordinator.Acknowledge(report.JobId, report.Sequence);
            Check(coordinator.NextReport() is null, "acknowledged terminal report leaves the outbox");
            coordinator.Handle(command);
            Check(coordinator.NextReport()?.State == "Completed", "replayed delivery resends the saved result");
        }
        Check(Directory.EnumerateFiles(plan.Destination, "*.zip", SearchOption.AllDirectories).Count() == 1, "replayed command never runs twice");
        var interrupted = Command(plan);
        var cancelled = Command(plan) with { CancelRequested = true };
        var journal = store.Load<JobJournal>(BackupCoordinator.JournalFile)!;
        store.Save(BackupCoordinator.JournalFile, new JobJournal([.. journal.Jobs.Select(j => j with { Acknowledged = true }),
            new(interrupted, new(interrupted.JobId, 1, "Copying", 12, 0, 10, 0, 1, "In progress"))]));
        await using var recovered = new BackupCoordinator(store, agentId);
        Check(recovered.NextReport()?.State == "Failed" && recovered.NextReport()!.Detail.Contains("interrupted"), "restart marks unconfirmed work interrupted instead of complete");
        recovered.Acknowledge(recovered.NextReport()!.JobId, recovered.NextReport()!.Sequence);
        recovered.Handle(cancelled);
        Check(recovered.NextReport()?.State == "Cancelled", "cancelled queued job never starts");
        Check(Directory.EnumerateFiles(plan.Destination, "*.zip", SearchOption.AllDirectories).Count() == 1, "recovery and cancellation preserve existing versions");
    });

    await Run("queued jobs survive restart, reject foreign reports, and honor the captured plan", async () =>
    {
        var plan = Plan("durable");
        await File.WriteAllTextAsync(Path.Combine(plan.Source, "durable.txt"), "Persisted backup command");
        var hostFolder = Path.Combine(root, "durable-host");
        await using var original = new HostServer(hostFolder, 0, IPAddress.Loopback);
        original.Start();
        var client = new AgentClient();
        var invitation = original.CreateInvitation("127.0.0.1");
        var identity = AgentClient.CreateCredentials(invitation, "Durable-PC");
        await client.PairAsync(invitation, identity);
        await client.PollAsync(identity, plan.Summary, null);
        var queued = original.QueueBackup(identity.AgentId);
        var port = original.Port;
        await original.DisposeAsync();
        await using var host = new HostServer(hostFolder, port, IPAddress.Loopback);
        host.Start();
        Check(host.GetJobs().Single().Command.JobId == queued.Command.JobId, "undelivered queued job survives host restart");
        var secondInvitation = host.CreateInvitation("127.0.0.1");
        var foreign = AgentClient.CreateCredentials(secondInvitation, "Another-PC");
        await client.PairAsync(secondInvitation, foreign);
        await RejectAsync(() => client.PollAsync(foreign, null, new(queued.Command.JobId, 1, "Preparing", 0, 0, 0, 0, 0, "Not this agent's job")));
        Check(host.GetJobs().Single().Report.State == "Queued", "another authenticated agent cannot report on this job");
        var store = new ProtectedStore(Path.Combine(root, "durable-agent"));
        store.Save("backup-plan.dat", plan);
        await using var coordinator = new BackupCoordinator(store, identity.AgentId);
        var poll = await client.PollAsync(identity, plan.Summary, null);
        Check(poll.Command?.JobId == queued.Command.JobId, "reconnecting client receives the original stable job ID");
        coordinator.Handle(poll.Command);
        await Until(() => coordinator.NextReport()?.State == "Completed", TimeSpan.FromSeconds(10));
        var report = coordinator.NextReport()!;
        var accepted = await client.PollAsync(identity, plan.Summary, report);
        coordinator.Acknowledge(accepted.AcknowledgedJobId, accepted.AcknowledgedSequence);
        await client.PollAsync(identity, plan.Summary, report);
        Check(host.GetJobs().Single().Report == report, "duplicate final report is acknowledged without changing the result");
        var next = host.QueueBackup(identity.AgentId);
        var changedPlan = BackupPlan.Create(plan.Source, Path.Combine(root, "new-local-destination"));
        store.Save("backup-plan.dat", changedPlan);
        var changed = await client.PollAsync(identity, changedPlan.Summary, null);
        coordinator.Handle(changed.Command);
        await Until(() => coordinator.NextReport()?.State == "Failed", TimeSpan.FromSeconds(10));
        await client.PollAsync(identity, changedPlan.Summary, coordinator.NextReport());
        Check(host.GetJobs().Last().Command.JobId == next.Command.JobId && host.GetJobs().Last().Report.State == "Failed", "changed client configuration rejects an outdated queued command");
        Check(host.GetSnapshots().Single(s => s.Id == identity.AgentId).LastSuccessfulJob?.Command.JobId == queued.Command.JobId,
            "failed changed-plan request preserves the last successful backup");
    });

    await Run("host-triggered backup through an actual configured agent process", async () =>
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AutoBack.slnx"))) repo = repo.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var executable = Path.Combine(repo!.FullName, "src", "AutoBack.Agent", "bin", configuration, "net10.0-windows", "AutoBack.Agent.exe");
        var plan = Plan("process");
        await File.WriteAllTextAsync(Path.Combine(plan.Source, "invoice.txt"), "Invoice 123 — local backup");
        var hostFolder = Path.Combine(root, "process-host");
        await using var initialHost = new HostServer(hostFolder, 0, IPAddress.Loopback);
        var host = initialHost;
        host.Start();
        var port = host.Port;
        var invitation = host.CreateInvitation("127.0.0.1");
        var invitationFile = Path.Combine(root, "client.autoback-invite");
        await File.WriteAllTextAsync(invitationFile, JsonSerializer.Serialize(invitation, Protocol.Json));
        var agentFolder = Path.Combine(root, "process-agent");
        Check(await Agent(executable, ["pair", "--invite", invitationFile, "--name", "Backup-PC", "--data-dir", agentFolder]) == 0, "agent paired");
        Check(await Agent(executable, ["configure", "--source", plan.Source, "--destination", plan.Destination, "--data-dir", agentFolder]) == 0, "client CLI saves actual source and destination");
        using var process = Start(executable, ["run", "--data-dir", agentFolder]);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await Until(() => host.GetSnapshots().Single().Configuration is not null, TimeSpan.FromSeconds(15));
            var id = host.GetSnapshots().Single().Id;
            var queued = host.QueueBackup(id);
            Reject(() => host.QueueBackup(id));
            await Until(() => host.GetJobs().Single().Report.State == "Completed", TimeSpan.FromSeconds(20));
            var completed = host.GetJobs().Single();
            Check(completed.Report.ArchivePath is not null && File.Exists(completed.Report.ArchivePath), "host receives completed result for a real archive");
            Check(host.GetSnapshots().Single().LastSuccessfulJob?.Command.JobId == queued.Command.JobId, "last successful local backup recorded separately");
            using (var zip = ZipFile.OpenRead(completed.Report.ArchivePath!))
            using (var reader = new StreamReader(zip.GetEntry("files/invoice.txt")!.Open()))
                Check((await reader.ReadToEndAsync()).Contains("Invoice 123"), "host-triggered archive contains the actual source file");
            await host.DisposeAsync();
            host = new HostServer(hostFolder, port, IPAddress.Loopback);
            host.Start();
            Check(host.GetJobs().Count == 1 && host.GetJobs()[0].Report.State == "Completed", "backup history survives host restart");
            await Until(() => host.GetSnapshots().Single().Online, TimeSpan.FromSeconds(15));
            var cancel = host.QueueBackup(id);
            host.CancelBackup(cancel.Command.JobId);
            await Until(() => host.GetJobs().Last().Report.State == "Cancelled", TimeSpan.FromSeconds(15));
            Check(Directory.EnumerateFiles(plan.Destination, "*.zip", SearchOption.AllDirectories).Count() == 1, "cancelled request preserves prior successful archive");
            using (var locked = new FileStream(Path.Combine(plan.Source, "invoice.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                host.QueueBackup(id);
                await Until(() => host.GetJobs().Last().Report.State == "Failed", TimeSpan.FromSeconds(15));
            }
            Check(host.GetSnapshots().Single().LastSuccessfulJob?.Command.JobId == queued.Command.JobId, "failed backup never replaces last successful result");
            Check(host.GetJobs().Last().Report.Detail.Length > 0, "actual file-access error reaches the host");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            _ = await stdout;
            _ = await stderr;
            await host.DisposeAsync();
        }
    });
    Console.WriteLine("ALL LOCAL BACKUP CHECKS PASSED");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
finally
{
    var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutoBack-backup-tests"));
    if (Directory.Exists(root) && Path.GetDirectoryName(root) == expectedParent && Guid.TryParseExact(Path.GetFileName(root), "N", out _))
        Directory.Delete(root, recursive: true);
}

BackupPlan Plan(string name)
{
    var source = Path.Combine(root, name, "source");
    var destination = Path.Combine(root, name, "destination");
    Directory.CreateDirectory(source);
    Directory.CreateDirectory(destination);
    return BackupPlan.Create(source, destination);
}
static BackupCommand Command(BackupPlan plan) => new(Guid.NewGuid(), plan.PlanId, DateTimeOffset.UtcNow);
static async Task<byte[]> HashFile(string path)
{
    await using var stream = File.OpenRead(path);
    return await SHA256.HashDataAsync(stream);
}
static void Check(bool condition, string text)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + text);
    Console.WriteLine("  PASS " + text);
}
static void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException) { return; }
    throw new InvalidOperationException("An invalid operation was unexpectedly accepted.");
}
static async Task RejectAsync(Func<Task> action)
{
    try { await action(); }
    catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or AgentRejectedException) { return; }
    throw new InvalidOperationException("An invalid operation was unexpectedly accepted.");
}
static async Task Run(string name, Func<Task> action) { Console.WriteLine("CHECK " + name); await action(); Console.WriteLine("PASS " + name); }
static async Task Until(Func<bool> condition, TimeSpan timeout)
{
    var timer = Stopwatch.StartNew();
    while (!condition()) { if (timer.Elapsed > timeout) throw new TimeoutException("Expected backup state was not reported."); await Task.Delay(50); }
}
static Process Start(string executable, string[] arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start)!;
}
static async Task<int> Agent(string executable, string[] arguments)
{
    using var process = Start(executable, arguments);
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    if (process.ExitCode != 0) Console.Error.WriteLine(await output + await error);
    return process.ExitCode;
}
