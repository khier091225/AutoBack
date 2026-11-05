using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AutoBack.Backup;
using AutoBack.Connection;
using AutoBack.Drive;
using AutoBack.Drive.Tests;

var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutoBack-automation-tests"));
var root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var repo = new DirectoryInfo(AppContext.BaseDirectory);
while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AutoBack.slnx"))) repo = repo.Parent;
var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
var agentExe = Path.Combine(repo!.FullName, "src", "AutoBack.Agent", "bin", configuration, "net10.0-windows", "AutoBack.Agent.exe");
const string passphrase = "Automation-only recovery sentinel 2026";
var agentId = Guid.NewGuid();
try
{
    await Run("encrypted streaming archive, tamper rejection, and portable recovery", async () =>
    {
        var store = Store("crypto"); var export = Path.Combine(root, "recovery.abkey");
        EncryptionVault.Enable(store, export, passphrase);
        var vault = EncryptionVault.Load(store); var key = vault.Active!;
        Check(vault.Enabled && !key.ToString()!.Contains(key.Material), "key material has no printable ToString");
        var fixture = await Fixture("encrypted", key, size: 2 * ArchiveFile.ChunkSize + 9);
        Check(fixture.Result.Encrypted && fixture.Result.ArchivePath.EndsWith(".abk"), "new backup uses an encrypted envelope");
        var bytes = await File.ReadAllBytesAsync(fixture.Result.ArchivePath);
        Check(!Encoding.UTF8.GetString(bytes).Contains("private source sentinel") && !Directory.EnumerateFiles(Path.GetDirectoryName(fixture.Result.ArchivePath)!).Any(p => p.EndsWith(".partial") || p.EndsWith(".zip")), "no plaintext ZIP or partial file left on disk");
        Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(export)).Contains(key.Material), "portable recovery file is password protected");
        var other = Store("recovery-on-other-client");
        var destination = Path.Combine(root, "crypto-recovered");
        await Reject(() => new RestoreEngine().RestoreAsync(fixture.Result.ArchivePath, destination, new("Local", "encrypted"), keys: EncryptionVault.Load(other).Resolve));
        await Reject(() => { EncryptionVault.Import(other, export, "incorrect recovery passphrase"); return Task.CompletedTask; });
        Check(EncryptionVault.Load(other).Keys.Count == 0, "wrong recovery passphrase cannot change the key store");
        EncryptionVault.Import(other, export, passphrase);
        Check(!EncryptionVault.Load(other).Enabled, "import enables recovery without changing new-backup settings");
        var recovered = await new RestoreEngine().RestoreAsync(fixture.Result.ArchivePath, destination, new("Local", "encrypted"), keys: EncryptionVault.Load(other).Resolve);
        Check((await Hash(Path.Combine(recovered.PayloadDirectory, "data.bin"))) == await Hash(Path.Combine(fixture.Plan.Source, "data.bin")), "another client recovers exact original bytes using the export");
        Check(recovered.ArchiveSha256 == fixture.Result.ArchiveSha256 && (await RestoreEngine.VerifyRestoredAsync(destination)).Files == 2, "restore report hashes the encrypted archive and verifies its payload");
        foreach (var scenario in new[] { "header", "chunk", "footer", "truncate", "append", "swap" })
        {
            var damaged = bytes.ToArray();
            if (scenario == "header") damaged[12] ^= 1;
            if (scenario == "chunk") damaged[64 + 200] ^= 1;
            if (scenario == "footer") damaged[^24] ^= 1;
            if (scenario == "truncate") damaged = damaged[..^20];
            if (scenario == "append") damaged = [..damaged, 0];
            if (scenario == "swap")
            {
                var length = ArchiveFile.ChunkSize + 16;
                var first = damaged.AsSpan(64, length).ToArray(); damaged.AsSpan(64 + length, length).CopyTo(damaged.AsSpan(64)); first.CopyTo(damaged.AsSpan(64 + length));
            }
            var path = Path.Combine(root, "damaged-" + scenario + ".abk"); await File.WriteAllBytesAsync(path, damaged);
            var output = Path.Combine(root, "reject-crypto-" + scenario);
            await Reject(() => new RestoreEngine().RestoreAsync(path, output, new("Local", "damaged"), keys: vault.Resolve));
            Check(!Directory.Exists(output), scenario + " tampering is not published");
        }
        await Reject(() => { EncryptionVault.Enable(store, export, passphrase); return Task.CompletedTask; });
        var failedStore = Store("failed-export");
        await Reject(() => { EncryptionVault.Enable(failedStore, export, passphrase); return Task.CompletedTask; });
        Check(!EncryptionVault.Load(failedStore).Enabled, "failed export never activates encryption");
        EncryptionVault.Disable(store);
        Check(!EncryptionVault.Load(store).Enabled && EncryptionVault.Load(store).Resolve(key.Id).Material == key.Material, "disabling new encryption retains recovery keys");
    });

    await Run("local retention ownership, pending uploads, survivor checks and exact pruning", async () =>
    {
        var fixture = await Fixture("retention", size: 1200);
        var jobs = new List<LocalJob> { fixture.Job };
        for (var n = 1; n <= 3; n++)
        {
            var command = new BackupCommand(Guid.NewGuid(), fixture.Plan.PlanId, fixture.Job.Command.CreatedUtc.AddMinutes(n));
            var result = await new LocalBackupEngine().CreateAsync(fixture.Plan, agentId, command, _ => { }); jobs.Add(Job(command, result));
        }
        var unrelated = Path.Combine(fixture.Plan.Destination, "unrelated.txt"); await File.WriteAllTextAsync(unrelated, "KEEP");
        var pruned = new HashSet<Guid>();
        var protectedJobs = new HashSet<Guid> { jobs[0].Command.JobId };
        var removed = await LocalRetention.PruneAsync(fixture.Plan, agentId, jobs, 2, protectedJobs, _ => throw new CryptographicException(), id => pruned.Add(id));
        Check(removed == 1 && File.Exists(jobs[0].Report.ArchivePath) && !File.Exists(jobs[1].Report.ArchivePath), "pending upload protected while one eligible old archive is pruned");
        jobs = jobs.Select(j => pruned.Contains(j.Command.JobId) ? j with { Report = j.Report with { ArchivePruned = true } } : j).ToList();
        removed = await LocalRetention.PruneAsync(fixture.Plan, agentId, jobs, 2, new HashSet<Guid>(), _ => throw new CryptographicException(), id => pruned.Add(id));
        Check(removed == 1 && File.Exists(jobs[2].Report.ArchivePath) && File.Exists(jobs[3].Report.ArchivePath) && await File.ReadAllTextAsync(unrelated) == "KEEP", "last two successful versions and unrelated data retained");
        var tampered = jobs[3].Report.ArchivePath!; var saved = await File.ReadAllBytesAsync(tampered); await File.WriteAllBytesAsync(tampered, [1, 2, 3]);
        await Reject(() => LocalRetention.PruneAsync(fixture.Plan, agentId, [jobs[2], jobs[3]], 1, new HashSet<Guid>(), _ => throw new CryptographicException(), _ => { }));
        Check(File.Exists(jobs[2].Report.ArchivePath), "a corrupt newest survivor prevents pruning the previous valid version");
        await File.WriteAllBytesAsync(tampered, saved);
        await Reject(() => LocalRetention.PruneAsync(fixture.Plan, agentId, [jobs[2] with { Report = jobs[2].Report with { ArchivePath = unrelated } }, jobs[3]], 1, new HashSet<Guid>(), _ => throw new CryptographicException(), _ => { }));
        Check(await File.ReadAllTextAsync(unrelated) == "KEEP", "foreign archive paths never cause deletion");
    });

    await Run("encrypted upload/download and bounded cloud retention", async () =>
    {
        var store = Store("cloud"); Seed(store);
        EncryptionVault.Enable(store, Path.Combine(root, "cloud-recovery.abkey"), passphrase);
        var fixture = await Fixture("cloud", EncryptionVault.Load(store).Active, size: 1000);
        using var google = new FakeGoogle(); using var http = new HttpClient(google);
        await using var uploads = new CloudUploadCoordinator(store, http, agentId, "Encryption-Test"); uploads.SetHostAuthorized(true);
        var jobs = new List<LocalJob>();
        for (var i = 0; i < 3; i++)
        {
            var command = new BackupCommand(Guid.NewGuid(), fixture.Plan.PlanId, DateTimeOffset.UtcNow.AddMinutes(i));
            var result = await new LocalBackupEngine().CreateAsync(fixture.Plan, agentId, command, _ => { }, encryptionKey: EncryptionVault.Load(store).Active);
            jobs.Add(Job(command, result)); uploads.Tick(jobs);
            await Until(() => uploads.AllUploads().Any(u => u.JobId == command.JobId && u.Report.State == "Completed"));
            await Until(() => !uploads.IsBusy);
        }
        var saved = uploads.AllUploads();
        var downloaded = await new DriveDownloader(store, http).DownloadAsync(saved[0].Report.FileId!, Path.Combine(root, "cloud-encrypted.download"));
        var restored = await new RestoreEngine().RestoreAsync(downloaded.Path, Path.Combine(root, "cloud-encrypted-recovered"), new("GoogleDrive", "test", downloaded.BackupJobId, downloaded.BackupAgentId), keys: EncryptionVault.Load(store).Resolve);
        Check(await Hash(Path.Combine(restored.PayloadDirectory, "data.bin")) == await Hash(Path.Combine(fixture.Plan.Source, "data.bin")), "encrypted bytes upload, download, decrypt and restore through the API-shaped transport");
        google.TamperRetentionMetadata = true;
        await Reject(() => new DriveRetention(store, http, agentId).PruneAsync(saved, 1, uploads.MarkTrashed));
        Check(google.TrashedFiles == 0, "changed cloud metadata prevents retention deletions");
        google.TamperRetentionMetadata = false;
        var count = await new DriveRetention(store, http, agentId).PruneAsync(saved, 1, uploads.MarkTrashed);
        Check(count == 2 && google.TrashedFiles == 2 && uploads.AllUploads().Count(u => u.Report.Trashed) == 2, "only two old confirmed client files moved to Drive Trash");
        Check(await new DriveRetention(store, http, agentId).PruneAsync(uploads.AllUploads(), 1, uploads.MarkTrashed) == 0, "retention restart does not repeat completed trash operations");
        await Reject(() => new DriveDownloader(store, http).DownloadAsync(saved[0].Report.FileId!, Path.Combine(root, "trashed.download")));
    });

    await Run("persisted daily schedule, catch-up, bounded retry and cancellation", async () =>
    {
        var clock = new TestClock(new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
        var directory = Path.Combine(root, "schedule-host"); var client = new AgentClient(); AgentCredentials identity; int port;
        await using (var host = new HostServer(directory, 0, IPAddress.Loopback, clock))
        {
            host.Start(); port = host.Port;
            var invite = host.CreateInvitation("127.0.0.1"); identity = AgentClient.CreateCredentials(invite, "Scheduled-PC"); await client.PairAsync(invite, identity);
            var fixture = await Fixture("schedule"); await client.PollAsync(identity, fixture.Plan.Summary, null);
            host.SetAutomation(new(true, "10:01", "UTC", 2, 5));
            host.TickAutomation(); Check(host.GetJobs().Count == 0, "new schedule waits for its next due time");
            clock.Advance(TimeSpan.FromMinutes(2)); host.TickAutomation(); host.TickAutomation();
            var first = host.GetJobs().Single(); Check(first.Trigger == "Schedule", "one durable daily command is queued");
            await client.PollAsync(identity, fixture.Plan.Summary, new(first.Command.JobId, 1, "Failed", 0, 0, 0, 0, 0, "File temporarily locked", Retryable: true));
            clock.Advance(TimeSpan.FromMinutes(4)); host.TickAutomation(); Check(host.GetJobs().Count == 1, "retry delay is respected");
            clock.Advance(TimeSpan.FromMinutes(1)); host.TickAutomation(); host.TickAutomation();
            var retry = host.GetJobs().Last(); Check(host.GetJobs().Count == 2 && retry.Command.RetryOf == first.Command.JobId && retry.Command.Attempt == 1, "retry creates a new immutable job and never duplicates a scheduled slot");
            await client.PollAsync(identity, fixture.Plan.Summary, new(retry.Command.JobId, 1, "Failed", 0, 0, 0, 0, 0, "File temporarily locked", Retryable: true));
            clock.Advance(TimeSpan.FromMinutes(5)); host.TickAutomation();
            var final = host.GetJobs().Last(); Check(final.Command.Attempt == 2, "second retry is the configured final attempt");
            await client.PollAsync(identity, fixture.Plan.Summary, new(final.Command.JobId, 1, "Failed", 0, 0, 0, 0, 0, "File still locked", Retryable: true));
            clock.Advance(TimeSpan.FromHours(2)); host.TickAutomation(); Check(host.GetJobs().Count == 3, "retry limit stops repeated backup failures");
        }
        await using (var restarted = new HostServer(directory, port, IPAddress.Loopback, clock))
        {
            restarted.Start(); restarted.TickAutomation(); Check(restarted.GetJobs().Count == 3 && restarted.AutomationPolicy.Enabled, "host restart preserves schedule settings and processed slots");
            clock.Advance(TimeSpan.FromDays(4)); restarted.TickAutomation(); restarted.TickAutomation();
            Check(restarted.GetJobs().Count == 4, "a multi-day outage catches up once without a backup storm");
            var queued = restarted.GetJobs().Last(); restarted.CancelBackup(queued.Command.JobId);
            var plan = (await Fixture("cancel-schedule")).Plan;
            await client.PollAsync(identity, plan.Summary, new(queued.Command.JobId, 1, "Cancelled", 0, 0, 0, 0, 0, "Operator cancelled"));
            clock.Advance(TimeSpan.FromMinutes(10)); restarted.TickAutomation(); Check(restarted.GetJobs().Count == 4, "cancelled work never retries");
        }
        var dst = new HostAutomationPolicy(true, "02:30", "Eastern Standard Time", Revision: Guid.NewGuid(), ActivatedUtc: DateTimeOffset.MinValue);
        Check(dst.LatestDue(new(2026, 3, 8, 8, 0, 0, TimeSpan.Zero))?.Utc == new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), "spring-forward invalid schedule time advances to the first valid minute");
    });

    await Run("retention annotations preserve successful results over authenticated TLS", async () =>
    {
        await using var host = new HostServer(Path.Combine(root, "expiration-host"), 0, IPAddress.Loopback); host.Start();
        var invite = host.CreateInvitation("127.0.0.1"); var identity = AgentClient.CreateCredentials(invite, "Retention-PC"); var client = new AgentClient();
        await client.PairAsync(invite, identity);
        var fixture = await Fixture("expiration"); await client.PollAsync(identity, fixture.Plan.Summary, null);
        var job = host.QueueBackup(identity.AgentId);
        var result = await new LocalBackupEngine().CreateAsync(fixture.Plan, identity.AgentId, job.Command, _ => { });
        var report = Job(job.Command, result).Report;
        var cloud = new CloudReport(job.Command.JobId, 1, "Completed", 100, 123, 123, "Remote verified", "retention-file", "https://drive.google.com/file/d/retention-file/view");
        await client.PollAsync(identity, fixture.Plan.Summary, report, cloudReport: cloud);
        await client.PollAsync(identity, fixture.Plan.Summary, report with { Sequence = 2, ArchivePruned = true }, cloudReport: cloud with { Sequence = 2, Trashed = true });
        Check(host.GetJobs().Single().Report is { State: "Completed", ArchivePruned: true } && host.GetJobs().Single().Cloud is { State: "Completed", Trashed: true }, "expiration is recorded without changing original successful results");
        await client.PollAsync(identity, fixture.Plan.Summary, report with { Sequence = 3, Detail = "Changed result" });
        Check(host.GetJobs().Single().Report.Detail == report.Detail && host.GetSnapshots().Single().LatestCloudJob is null, "terminal results stay immutable and expired clouds do not appear as the latest available upload");
    });

    await Run("actual three-client console pilot: schedule, encryption, offline queue and reconnect", async () =>
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var host = new HostServer(Path.Combine(root, "pilot-host"), 0, IPAddress.Loopback, clock); host.Start();
        var workers = new List<(Process Process, Task<string> Out, Task<string> Error)>();
        var stores = new List<ProtectedStore>(); var identities = new List<AgentCredentials>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var store = Store("pilot-agent-" + i); stores.Add(store);
                var invite = host.CreateInvitation("127.0.0.1"); var identity = AgentClient.CreateCredentials(invite, "Pilot-PC-" + i); identities.Add(identity);
                await new AgentClient().PairAsync(invite, identity); store.Save("agent.dat", identity);
                var fixture = await Fixture("pilot-" + i, size: 4000); store.Save("backup-plan.dat", fixture.Plan);
                if (i == 1) EncryptionVault.Enable(store, Path.Combine(root, "pilot-client.abkey"), passphrase);
                workers.Add(StartWorker(i));
            }
            await Until(() => host.GetSnapshots().Count(s => s.Online && s.Configuration is not null) == 3);
            var nextMinute = clock.GetUtcNow().AddMinutes(1).ToString("HH:mm");
            host.SetAutomation(new(true, nextMinute, "UTC")); clock.Advance(TimeSpan.FromMinutes(2)); host.TickAutomation();
            await Until(() => host.GetJobs().Count == 3 && host.GetJobs().All(j => j.Report.State == "Completed"));
            Check(host.GetJobs().All(j => File.Exists(j.Report.ArchivePath)) && host.GetJobs().Count(j => j.Report.Encrypted) == 1, "three real processes complete scheduled backups with their own encryption settings");
            foreach (var job in host.GetJobs())
            {
                var index = identities.FindIndex(c => c.AgentId == job.AgentId);
                var recovered = await new RestoreEngine().RestoreAsync(job.Report.ArchivePath!, Path.Combine(root, "pilot-recovery-" + index), new("Local", "pilot"), keys: EncryptionVault.Load(stores[index]).Resolve);
                Check(recovered.BackupAgentId == job.AgentId, "recovery verified for pilot client " + index);
            }
            workers[2].Process.Kill(entireProcessTree: true); await workers[2].Process.WaitForExitAsync(); await workers[2].Out; await workers[2].Error; workers[2].Process.Dispose();
            clock.Advance(TimeSpan.FromDays(1)); host.TickAutomation();
            await Until(() => host.GetJobs().Count == 6 && host.GetJobs().Count(j => j.Report.State == "Completed") == 5);
            Check(host.GetJobs().Single(j => j.AgentId == identities[2].AgentId && !BackupStates.Terminal(j.Report.State)).Report.State == "Queued", "offline client keeps its durable scheduled job");
            workers[2] = StartWorker(2);
            await Until(() => host.GetJobs().Count == 6 && host.GetJobs().All(j => j.Report.State == "Completed"));
            Check(host.GetJobs().Select(j => j.Command.JobId).Distinct().Count() == 6 && stores.All(s => s.Load<JobJournal>("jobs.dat")!.Jobs.Count == 2), "reconnected clients complete exactly one backup per due day");
            var output = Path.Combine(repo.FullName, "artifacts", "automation-validation"); Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "multi-client-result.json"), System.Text.Json.JsonSerializer.Serialize(new
            { verifiedAtUtc = DateTimeOffset.UtcNow, clients = 3, completedBackups = 6, encryptedClient = true, restoreVerifiedClients = 3, offlineQueueAndReconnect = true, physicalPCs = 1, serviceInstallationTested = false }, Protocol.Json));
        }
        finally
        {
            foreach (var worker in workers)
            {
                try { if (!worker.Process.HasExited) worker.Process.Kill(entireProcessTree: true); await worker.Process.WaitForExitAsync(); await worker.Out; await worker.Error; }
                catch (InvalidOperationException) { }
                worker.Process.Dispose();
            }
        }
        (Process, Task<string>, Task<string>) StartWorker(int i)
        {
            var process = Start(["run", "--data-dir", Path.Combine(root, "pilot-agent-" + i)]);
            return (process, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
        }
    });

    await Run("actual protection CLI and retention status without secret arguments", async () =>
    {
        var directory = Path.Combine(root, "cli"); var export = Path.Combine(root, "cli.abkey");
        var result = await Cli(["encryption-enable", "--data-dir", directory, "--recovery-file", export, "--password-stdin"], passphrase);
        Check(result.Exit == 0 && !result.Output.Contains(passphrase), "CLI creates a recoverable key without logging its passphrase");
        Check((await Cli(["retention-configure", "--data-dir", directory, "--keep-local", "14", "--keep-cloud", "30"])).Exit == 0, "explicit retention counts persist");
        Check((await Cli(["retention-configure", "--data-dir", directory, "--keep-local", "-1"])).Exit != 0 && RetentionPolicy.Load(new(directory)).KeepLocal == 14, "invalid retention counts preserve previous policy");
        Check((await Cli(["protection-status", "--data-dir", directory])).Output.Contains("AES-256-GCM"), "CLI reports real protection state");
        var faultedStore = Store("faulted-worker");
        var credential = new AgentCredentials("127.0.0.1", 47832, new string('0', 64), Guid.NewGuid(), Protocol.NewSecret(), "Fault-Test");
        faultedStore.Save("agent.dat", credential); faultedStore.Save("jobs.dat", new JobJournal(null!));
        var failure = await Cli(["run", "--data-dir", Path.Combine(root, "faulted-worker")]);
        Check(failure.Exit != 0 && !failure.Output.Contains(credential.Secret), "fatal console worker failure returns nonzero without printing credentials");
    });
    Console.WriteLine("ALL AUTOMATION CHECKS PASSED (three real clients on ONE physical PC; Google HTTP simulated)"); return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
finally
{
    if (Directory.Exists(root) && Path.GetDirectoryName(Path.GetFullPath(root)) == parent && Guid.TryParseExact(Path.GetFileName(root), "N", out _)) Directory.Delete(root, true);
}

ProtectedStore Store(string name) => new(Path.Combine(root, name));
async Task<(BackupPlan Plan, LocalJob Job, BackupResult Result)> Fixture(string name, ArchiveKey? key = null, int size = 512)
{
    var source = Path.Combine(root, name, "source"); Directory.CreateDirectory(source);
    await File.WriteAllBytesAsync(Path.Combine(source, "data.bin"), RandomNumberGenerator.GetBytes(size));
    await File.WriteAllTextAsync(Path.Combine(source, "private.txt"), "private source sentinel");
    var plan = BackupPlan.Create(source, Path.Combine(root, name, "backups"));
    var command = new BackupCommand(Guid.NewGuid(), plan.PlanId, DateTimeOffset.UtcNow);
    var result = await new LocalBackupEngine().CreateAsync(plan, agentId, command, _ => { }, encryptionKey: key);
    return (plan, Job(command, result), result);
}
static LocalJob Job(BackupCommand command, BackupResult result) => new(command,
    new(command.JobId, 1, "Completed", 100, result.Bytes, result.Bytes, result.Files, result.Files, "Verified backup", result.ArchivePath, Encrypted: result.Encrypted, ArchiveSha256: result.ArchiveSha256));
static void Seed(ProtectedStore store) => store.Save("drive-account.dat", new DriveAccount { ClientId = "test.apps.googleusercontent.com", ClientSecret = "CLIENT_SENTINEL", RefreshToken = "REFRESH_SENTINEL", Email = "backup@example.invalid", UserId = "user-one" });
Process Start(string[] arguments, bool input = false)
{
    var start = new ProcessStartInfo(agentExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input };
    foreach (var argument in arguments) start.ArgumentList.Add(argument); return Process.Start(start)!;
}
async Task<(int Exit, string Output)> Cli(string[] arguments, string? password = null)
{
    using var process = Start(arguments, password is not null);
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    if (password is not null) { await process.StandardInput.WriteLineAsync(password); process.StandardInput.Close(); }
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(entireProcessTree: true); throw; }
    return (process.ExitCode, await stdout + await stderr);
}
static async Task<string> Hash(string path) { await using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input)); }
static void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException("FAIL: " + description); Console.WriteLine("  PASS " + description); }
static async Task Run(string description, Func<Task> test) { Console.WriteLine("CHECK " + description); await test(); Console.WriteLine("PASS " + description); }
static async Task Until(Func<bool> ready)
{
    var timer = Stopwatch.StartNew(); while (!ready()) { if (timer.Elapsed > TimeSpan.FromSeconds(35)) throw new TimeoutException("Operation did not reach the expected state."); await Task.Delay(50); }
}
static async Task Reject(Func<Task> operation)
{
    try { await operation(); }
    catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or CryptographicException or DriveException) { return; }
    throw new InvalidOperationException("Expected operation rejection did not occur.");
}
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private long _ticks = now.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
}
