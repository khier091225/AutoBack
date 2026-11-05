using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoBack.Backup;
using AutoBack.Connection;
using AutoBack.Drive;

var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutoBack-restore-tests"));
var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var source = Path.Combine(root, "source");
    Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
    await File.WriteAllTextAsync(Path.Combine(source, "hello.txt"), "Restored content: kumusta, 世界!\r\n", new UTF8Encoding(false));
    await File.WriteAllBytesAsync(Path.Combine(source, "nested", "data.bin"), RandomNumberGenerator.GetBytes(320_001));
    await File.WriteAllBytesAsync(Path.Combine(source, "zero.txt"), []);
    // A source file may share the name of restore metadata; it stays inside the payload.
    await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), "source-owned document");
    var original = await FileSet(source);
    var plan = BackupPlan.Create(source, Path.Combine(root, "backups"));
    var agentId = Guid.NewGuid();
    var command = new BackupCommand(Guid.NewGuid(), plan.PlanId, DateTimeOffset.UtcNow);
    var backup = await new LocalBackupEngine().CreateAsync(plan, agentId, command, _ => { });
    var archiveBytes = await File.ReadAllBytesAsync(backup.ArchivePath);
    var archiveHash = Convert.ToHexString(SHA256.HashData(archiveBytes));
    BackupManifest manifest;
    using (var zip = ZipFile.OpenRead(backup.ArchivePath)) manifest = await RestoreEngine.ReadManifestAsync(zip, new());
    var engine = new RestoreEngine();
    var destination = Path.Combine(root, "restored");

    await Run("real ZIP recovery and independent disk readback", async () =>
    {
        var states = new List<string>();
        var result = await engine.RestoreAsync(backup.ArchivePath, destination, new("Local", backup.ArchivePath), p => states.Add(p.State));
        Check(result.Files == 4 && result.Directories == 2 && result.BackupJobId == command.JobId && result.BackupAgentId == agentId, "backup identity and complete counts retained");
        Check(result.ArchiveSha256 == archiveHash && states.Last() == "Completed" && states.Contains("Verifying"), "archive SHA and readback phase confirmed");
        Check((await FileSet(result.PayloadDirectory)).SequenceEqual(original), "all recovered bytes match the original source");
        Check(Directory.Exists(Path.Combine(result.PayloadDirectory, "nested", "empty")), "empty directories retained");
        foreach (var file in manifest.Files)
            Check(File.GetLastWriteTimeUtc(Path.Combine(result.PayloadDirectory, file.Path)) == file.LastWriteUtc, "original file timestamp: " + file.Path);
        Check((await RestoreEngine.VerifyRestoredAsync(destination)).Files == 4, "standalone verification passes a complete recovery");
        Check((await FileSet(source)).SequenceEqual(original) && (await File.ReadAllBytesAsync(backup.ArchivePath)).SequenceEqual(archiveBytes), "source files and source ZIP remain unchanged");
    });

    await Run("no overwrite, no source overlap, and publication race", async () =>
    {
        await Reject(() => engine.RestoreAsync(backup.ArchivePath, destination, new("Local", "test")));
        var empty = Path.Combine(root, "existing-empty"); Directory.CreateDirectory(empty);
        await Reject(() => engine.RestoreAsync(backup.ArchivePath, empty, new("Local", "test")));
        await Reject(() => engine.RestoreAsync(backup.ArchivePath, Path.Combine(source, "child"), new("Local", "test")));
        var occupied = Path.Combine(root, "occupied.txt"); await File.WriteAllTextAsync(occupied, "KEEP");
        await Reject(() => engine.RestoreAsync(backup.ArchivePath, occupied, new("Local", "test")));
        Check(await File.ReadAllTextAsync(occupied) == "KEEP", "existing file destination preserved");
        var raced = Path.Combine(root, "raced");
        await Reject(() => engine.RestoreAsync(backup.ArchivePath, raced, new("Local", "test"), p =>
        { if (p.State == "Publishing") { Directory.CreateDirectory(raced); File.WriteAllText(Path.Combine(raced, "owned.txt"), "PRESERVE"); } }));
        Check(await File.ReadAllTextAsync(Path.Combine(raced, "owned.txt")) == "PRESERVE", "concurrent destination creation never overwrites user data");
        NoStaging();
    });

    await Run("modified, missing, extra files/directories and corrupt report fail verification", async () =>
    {
        var hello = Path.Combine(destination, "files", "hello.txt");
        var content = await File.ReadAllBytesAsync(hello);
        var changed = content.ToArray(); changed[0] ^= 1; await File.WriteAllBytesAsync(hello, changed);
        await Reject(() => RestoreEngine.VerifyRestoredAsync(destination));
        await File.WriteAllBytesAsync(hello, content);
        File.Move(hello, hello + ".missing"); await Reject(() => RestoreEngine.VerifyRestoredAsync(destination)); File.Move(hello + ".missing", hello);
        var extra = Path.Combine(destination, "files", "extra"); await File.WriteAllTextAsync(extra, "extra");
        await Reject(() => RestoreEngine.VerifyRestoredAsync(destination)); File.Delete(extra);
        Directory.CreateDirectory(extra); await Reject(() => RestoreEngine.VerifyRestoredAsync(destination)); Directory.Delete(extra);
        var reportFile = Path.Combine(destination, "restore-report.json"); var reportText = await File.ReadAllTextAsync(reportFile);
        var report = JsonSerializer.Deserialize<RestoreResult>(reportText, Protocol.Json)!;
        await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(report with { Files = 100 }, Protocol.Json));
        await Reject(() => RestoreEngine.VerifyRestoredAsync(destination)); await File.WriteAllTextAsync(reportFile, reportText);
        Check((await RestoreEngine.VerifyRestoredAsync(destination)).State == "Completed", "repaired payload verifies without rewriting original report");
    });

    await Run("unsafe and inconsistent manifests/ZIPs rejected without publishing", async () =>
    {
        foreach (var unsafePath in new[] { "../escape", "a/../escape", "/absolute", "C:/escape", "a\\b", "a:stream", "NUL.txt", "com1.txt", "LPT².doc", "trailing.", "trailing ", "a//b", "./file", "x\u0001" })
        {
            await Reject(() => TryManifest(manifest with { Directories = [], Files = [manifest.Files[0] with { Path = unsafePath }] }));
        }
        await Reject(() => TryManifest(manifest with { FormatVersion = 22 }));
        await Reject(() => TryManifest(manifest with { JobId = Guid.Empty }));
        await Reject(() => TryManifest(manifest with { AgentId = Guid.Empty }));
        await Reject(() => TryManifest(manifest with { Files = [manifest.Files[0], manifest.Files[0] with { Path = manifest.Files[0].Path.ToUpperInvariant() }] }));
        await Reject(() => TryManifest(manifest with { Directories = [manifest.Files[0].Path], Files = [manifest.Files[0]] }));
        await Reject(() => TryManifest(manifest with { Directories = [] }));
        await Reject(() => TryManifest(manifest with { Files = [manifest.Files[0] with { Length = -1 }] }));
        await Reject(() => TryManifest(manifest with { Files = [manifest.Files[0] with { Sha256 = "invalid" }] }));
        foreach (var scenario in new[] { "extra", "missing", "duplicate", "symlink", "wrong-length", "bad-hash", "corrupt" })
        {
            var zipPath = Path.Combine(root, "bad-" + scenario + ".zip"); File.Copy(backup.ArchivePath, zipPath);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
            {
                var file = manifest.Files.First(f => f.Length > 0);
                var entry = zip.GetEntry("files/" + file.Path)!;
                if (scenario == "extra") zip.CreateEntry("../escape.txt");
                if (scenario == "missing") entry.Delete();
                if (scenario == "duplicate") zip.CreateEntry(entry.FullName);
                if (scenario == "symlink") entry.ExternalAttributes = (0xA1FF << 16);
                if (scenario is "wrong-length" or "bad-hash")
                {
                    entry.Delete(); var replacement = zip.CreateEntry("files/" + file.Path);
                    await using var writer = replacement.Open();
                    await writer.WriteAsync(new byte[scenario == "wrong-length" ? 1 : checked((int)file.Length)]);
                }
            }
            if (scenario == "corrupt") await File.WriteAllBytesAsync(zipPath, archiveBytes[..20]);
            var output = Path.Combine(root, "reject-" + scenario);
            await Reject(() => engine.RestoreAsync(zipPath, output, new("Local", "test")));
            Check(!Directory.Exists(output), scenario + " is not published");
            NoStaging();
        }
        Check(!File.Exists(Path.Combine(root, "escape.txt")), "malicious paths never escape staging");
    });

    await Run("limits, cancellation, backup identity, and empty backup", async () =>
    {
        await Reject(() => engine.RestoreAsync(backup.ArchivePath, Path.Combine(root, "limited"), new("Local", "test"), limits: new(MaxTotalBytes: 2)));
        await Reject(() => engine.RestoreAsync(backup.ArchivePath, Path.Combine(root, "limited-entries"), new("Local", "test"), limits: new(MaxEntries: 2)));
        await Reject(() => engine.RestoreAsync(backup.ArchivePath, Path.Combine(root, "limited-manifest"), new("Local", "test"), limits: new(MaxManifestBytes: 2)));
        await Reject(() => engine.RestoreAsync(backup.ArchivePath, Path.Combine(root, "wrong-job"), new("GoogleDrive", "test", Guid.NewGuid(), agentId)));
        await Reject(() => engine.RestoreAsync(backup.ArchivePath, Path.Combine(root, "wrong-agent"), new("GoogleDrive", "test", command.JobId, Guid.NewGuid())));
        using var cancellation = new CancellationTokenSource();
        await Reject(() => engine.RestoreAsync(backup.ArchivePath, Path.Combine(root, "cancelled"), new("Local", "test"),
            p => { if (p.State == "Verifying") cancellation.Cancel(); }, cancellation.Token));
        Check(!Directory.Exists(Path.Combine(root, "cancelled")), "cancelled restore is not published"); NoStaging();
        var empty = Path.Combine(root, "empty-source"); Directory.CreateDirectory(empty);
        var emptyPlan = BackupPlan.Create(empty, Path.Combine(root, "empty-backups"));
        var emptyResult = await new LocalBackupEngine().CreateAsync(emptyPlan, agentId, new(Guid.NewGuid(), emptyPlan.PlanId, DateTimeOffset.UtcNow), _ => { });
        var recovered = await engine.RestoreAsync(emptyResult.ArchivePath, Path.Combine(root, "empty-restored"), new("Local", "empty"));
        Check(recovered.Files == 0 && recovered.Bytes == 0 && (await RestoreEngine.VerifyRestoredAsync(recovered.Destination)).State == "Completed", "empty backups restore and verify correctly");
    });

    await Run("authenticated Drive download, corruption, metadata change and failed downloads", async () =>
    {
        foreach (var scenario in new[] { "success", "hash", "truncated", "oversized", "changed", "not-autoback", "missing", "authorization", "cancelled", "limit", "wrong-id" })
        {
            var store = new ProtectedStore(Path.Combine(root, "drive-store-" + scenario));
            store.Save("drive-account.dat", new DriveAccount { ClientId = "test.apps.googleusercontent.com", ClientSecret = "TEST_CLIENT", RefreshToken = "TEST_REFRESH", UserId = "test-user", Email = "test@example.invalid" });
            using var handler = new DownloadGoogle(archiveBytes, command.JobId, agentId, scenario);
            using var http = new HttpClient(handler);
            var downloader = new DriveDownloader(store, http);
            var output = Path.Combine(root, "download-" + scenario + ".zip");
            using var cancel = new CancellationTokenSource();
            async Task<DownloadedBackup> Download() => await downloader.DownloadAsync("https://drive.google.com/file/d/test-file/view", output,
                _ => { if (scenario == "cancelled") cancel.Cancel(); }, cancel.Token, scenario == "limit" ? 1 : 1L << 40);
            if (scenario == "success")
            {
                var downloaded = await Download();
                Check(downloaded.Sha256 == archiveHash && downloaded.BackupJobId == command.JobId && handler.AuthorizedMediaRequests == 1, "authenticated cloud bytes match original archive and identity");
                var restored = await engine.RestoreAsync(downloaded.Path, Path.Combine(root, "cloud-restored"), new("GoogleDrive", "test-file", downloaded.BackupJobId, downloaded.BackupAgentId));
                Check((await FileSet(restored.PayloadDirectory)).SequenceEqual(original), "cloud download recovers the original complete file set");
                await Reject(Download); // Existing verified downloads cannot be overwritten.
            }
            else { await Reject(Download); Check(!File.Exists(output), scenario + " cloud failure does not publish a download"); }
            Check(!Directory.EnumerateFiles(root, "download-" + scenario + ".zip.*.partial").Any(), "download staging cleaned: " + scenario);
        }
        foreach (var id in new[] { "https://evil.invalid/file/d/test", "http://drive.google.com/file/d/test/view", "https://drive.google.com.evil.invalid/file/d/test/view", "../test", "test?x" })
            await Reject(() => Task.FromResult(DriveDownloader.ParseFileId(id)));
    });

    await Run("client CLI, protected history, host reporting and interrupted operations", async () =>
    {
        var cliDirectory = Path.Combine(root, "cli-data");
        var cliDestination = Path.Combine(root, "cli-restored");
        var store = new ProtectedStore(cliDirectory);
        Check((await Cli("restore", "--archive", backup.ArchivePath, "--destination", cliDestination)).Exit == 0, "actual agent restore command succeeds");
        Check(RestoreHistory.Latest(store)?.State == "Completed", "completed result persisted in protected client storage");
        Check((await Cli("verify-restore", "--destination", cliDestination)).Exit == 0, "actual client verification command succeeds");
        Check((await Cli("restore-status")).Text.Contains("Completed"), "client status reports verified completion");
        Check((await Cli("restore", "--archive", backup.ArchivePath, "--destination", cliDestination)).Exit != 0, "CLI never overwrites an existing recovery folder");
        await File.WriteAllTextAsync(Path.Combine(cliDestination, "files", "hello.txt"), "corrupted");
        Check((await Cli("verify-restore", "--destination", cliDestination)).Exit != 0 && RestoreHistory.Latest(store)?.State == "Failed", "failed verification is recorded without success");
        var unfinished = new RestoreSummary(Guid.NewGuid(), "Restoring", "Local", cliDestination, 10, 1, 1, "Starting", DateTimeOffset.UtcNow);
        RestoreHistory.Save(store, new(unfinished, "test")); RestoreHistory.RecoverInterrupted(store);
        Check(RestoreHistory.Latest(store)?.State == "Interrupted", "unfinished recovery is marked interrupted on restart");
        await using var host = new HostServer(Path.Combine(root, "host"), 0, IPAddress.Loopback); host.Start();
        var invitation = host.CreateInvitation("127.0.0.1"); var identity = AgentClient.CreateCredentials(invitation, "Restore-PC"); var client = new AgentClient();
        await client.PairAsync(invitation, identity);
        var summary = RestoreHistory.Read(store).Operations.First().Summary;
        await client.PollAsync(identity, null, null, latestRestore: summary);
        Check(host.GetSnapshots().Single().LatestRestore == summary && !host.GetJobs().Any(), "restore result travels over authenticated TLS independently of backup jobs");
        await client.HeartbeatAsync(identity);
        Check(host.GetSnapshots().Single().LatestRestore == summary, "legacy heartbeat retains the reported restore result");
        await Reject(() => client.PollAsync(identity, null, null, latestRestore: summary with { Percent = 5 }));
        Check(host.GetSnapshots().Single().LatestRestore == summary, "invalid completion summary cannot replace verified state");

        async Task<(int Exit, string Text)> Cli(params string[] arguments)
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AutoBack.slnx"))) repo = repo.Parent;
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var assembly = Path.Combine(repo!.FullName, "src", "AutoBack.Agent", "bin", configuration, "net10.0-windows", "AutoBack.Agent.dll");
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(assembly); foreach (var arg in arguments) start.ArgumentList.Add(arg);
            start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(cliDirectory);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            return (process.ExitCode, await stdout + await stderr);
        }
    });
    Console.WriteLine("ALL RESTORE CHECKS PASSED (Drive HTTP simulated; real filesystem, agent CLI and TLS host)");
    return 0;

    Task TryManifest(BackupManifest value) { RestoreEngine.ValidateManifest(value, new()); return Task.CompletedTask; }
    void NoStaging() => Check(!Directory.EnumerateFileSystemEntries(root, ".autoback-restore-*.partial").Any(), "only owned temporary restore directories cleaned");
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
finally
{
    if (Path.GetDirectoryName(Path.GetFullPath(root)) == parent && Guid.TryParseExact(Path.GetFileName(root), "N", out _) && Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}

static async Task<string[]> FileSet(string directory)
{
    var rows = new List<string>();
    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        await using var stream = File.OpenRead(file);
        rows.Add(Path.GetRelativePath(directory, file) + "|" + Convert.ToHexString(await SHA256.HashDataAsync(stream)));
    }
    return rows.ToArray();
}
static void Check(bool okay, string description)
{ if (!okay) throw new InvalidOperationException("FAIL: " + description); Console.WriteLine("  PASS " + description); }
static async Task Run(string description, Func<Task> test) { Console.WriteLine("CHECK " + description); await test(); Console.WriteLine("PASS " + description); }
static async Task Reject(Func<Task> operation)
{
    try { await operation(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or DriveException or AgentRejectedException or OperationCanceledException) { return; }
    throw new InvalidOperationException("Expected rejection did not occur.");
}

sealed class DownloadGoogle(byte[] data, Guid job, Guid agent, string scenario) : HttpMessageHandler
{
    public int AuthorizedMediaRequests { get; private set; }
    private int _metadataReads;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        if (uri.Host == "oauth2.googleapis.com") return Task.FromResult(Json(new { access_token = "TEST_ACCESS", expires_in = 3600, token_type = "Bearer" }));
        if (request.Headers.Authorization?.ToString() != "Bearer TEST_ACCESS") throw new InvalidOperationException("Drive request lacks authentication.");
        if (scenario == "authorization") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new { error = "unauthorized" }) });
        if (scenario == "missing") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        if (uri.Query == "?alt=media")
        {
            AuthorizedMediaRequests++;
            var bytes = data.ToArray();
            if (scenario == "hash") bytes[0] ^= 1;
            if (scenario == "truncated") bytes = bytes[..^1];
            if (scenario == "oversized") bytes = [..bytes, 1];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
        _metadataReads++;
        var sha = Convert.ToHexString(SHA256.HashData(data));
        var props = new Dictionary<string, string> { ["autobackJob"] = job.ToString("N"), ["autobackAgent"] = agent.ToString("N"), ["autobackSha256"] = sha };
        if (scenario == "not-autoback") props.Clear();
        return Task.FromResult(Json(new { id = scenario == "wrong-id" ? "other-id" : "test-file", mimeType = "application/zip", size = data.LongLength.ToString(),
            md5Checksum = scenario == "changed" && _metadataReads > 1 ? new string('0', 32) : Convert.ToHexString(MD5.HashData(data)), sha256Checksum = sha, trashed = false, appProperties = props }));
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}
