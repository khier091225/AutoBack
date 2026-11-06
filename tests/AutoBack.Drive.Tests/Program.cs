using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBack.Backup;
using AutoBack.Connection;
using AutoBack.Drive;
using AutoBack.Drive.Tests;

var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutoBack-drive-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
try
{
    await Run("Desktop OAuth PKCE, loopback state, protected credentials, and refresh rejection", async () =>
    {
        Check(GoogleAuthorization.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "PKCE matches the RFC 7636 test vector");
        var store = Store("oauth");
        var google = new FakeGoogle();
        using var http = new HttpClient(google);
        var auth = new GoogleAuthorization(store, http);
        var file = Path.Combine(root, "desktop.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { installed = new { client_id = "test.apps.googleusercontent.com", client_secret = "CLIENT_SECRET_SENTINEL" } }));
        await auth.ConnectAsync(file, openBrowser: async uri =>
        {
            var parameters = FakeGoogle.Parse(uri.Query.TrimStart('?'));
            google.ExpectedChallenge = parameters["code_challenge"];
            Check(parameters["scope"] == DriveHttp.Scope && parameters["code_challenge_method"] == "S256", "sign-in requests only drive.file with PKCE");
            Check(new Uri(parameters["redirect_uri"]).Host == "127.0.0.1", "callback listens on loopback only");
            using var browser = new HttpClient();
            using var rejected = await browser.GetAsync(parameters["redirect_uri"] + "?code=attack&state=wrong");
            Check(rejected.StatusCode == HttpStatusCode.BadRequest, "incorrect OAuth state is rejected");
            using var accepted = await browser.GetAsync(parameters["redirect_uri"] + "?code=test-code&state=" + Uri.EscapeDataString(parameters["state"]));
            Check(accepted.IsSuccessStatusCode, "valid OAuth callback accepted");
        });
        Check(google.CodeVerifierChecked && auth.Status().State == "Connected", "authorization code is exchanged with the original verifier");
        var account = store.Load<DriveAccount>("drive-account.dat")!;
        Check(account.RefreshToken == "REFRESH_SENTINEL" && !account.ToString()!.Contains(account.RefreshToken), "credentials are stored without a token-revealing ToString");
        var ciphertext = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "oauth", "drive-account.dat")));
        Check(!ciphertext.Contains("REFRESH_SENTINEL") && !ciphertext.Contains("CLIENT_SECRET_SENTINEL"), "refresh token and client secret are DPAPI protected");
        google.RejectRefresh = true;
        await Reject(() => new GoogleTokenProvider(store, http).GetAsync(account.Key, false, CancellationToken.None), DriveFailure.Authorization);
        Check(auth.Status().State == "Needs sign-in", "revoked refresh grant is surfaced as needs sign-in");
        File.Delete(file);
        Uri? signInLink = null;
        Check(auth.CanReconnect, "saved OAuth app configuration is available for reconnection");
        await auth.ConnectAsync(openBrowser: async uri =>
        {
            var parameters = FakeGoogle.Parse(uri.Query.TrimStart('?'));
            google.ExpectedChallenge = parameters["code_challenge"];
            Check(parameters["client_id"] == "test.apps.googleusercontent.com", "reconnection reuses the protected Desktop client without the original JSON");
            using var browser = new HttpClient();
            using var accepted = await browser.GetAsync(parameters["redirect_uri"] + "?code=reconnect-code&state=" + Uri.EscapeDataString(parameters["state"]));
            Check(accepted.IsSuccessStatusCode, "reconnection receives a fresh browser callback");
        }, signInUrlReady: uri => signInLink = uri);
        Check(auth.Status().State == "Connected" && signInLink?.Host == "accounts.google.com" && !signInLink.AbsoluteUri.Contains("CLIENT_SECRET_SENTINEL"), "fresh authorization reconnects and exposes a browser link without persistent secrets");
        var savedAccount = File.ReadAllBytes(Path.Combine(root, "oauth", "drive-account.dat"));
        using (var cancel = new CancellationTokenSource())
        {
            try { await auth.ConnectAsync(cancellationToken: cancel.Token, openBrowser: uri => { cancel.Cancel(); return Task.CompletedTask; }); throw new Exception("Expected cancelled reconnect."); }
            catch (OperationCanceledException) { }
        }
        Check(savedAccount.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "oauth", "drive-account.dat"))), "cancelled reconnection preserves the saved Google account byte for byte");
        auth.Disconnect();
        Check(auth.Status().State == "Not connected", "disconnect removes local credentials");
    });

    await Run("resumable upload survives restart without duplicating the remote backup", async () =>
    {
        var fixture = await Backup("resume");
        var store = Store("resume-store");
        Seed(store);
        var google = new FakeGoogle { LoseFirstChunkResponse = true, RejectFirstApiToken = true };
        using var http = new HttpClient(google);
        await using (var first = new CloudUploadCoordinator(store, http, fixture.AgentId, "PC-Test"))
        {
            first.SetHostAuthorized(true);
            first.Tick([fixture.Job]);
            await Until(() => first.NextReport()?.State == "Retrying" && !first.IsBusy);
            Check(fixture.Job.Report.State == "Completed" && File.Exists(fixture.Path), "network failure preserves successful local backup");
        }
        var saved = store.Load<UploadJournal>("drive-uploads.dat")!.Uploads.Single();
        Check(saved.Checkpoint.SessionUri is not null && saved.Checkpoint.FileId is not null, "file ID and session checkpoint are persisted");
        var encryptedSession = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "resume-store", "drive-uploads.dat")));
        Check(!encryptedSession.Contains("secret-session"), "resumable URL is encrypted at rest");
        CloudUploadCoordinator.RetrySaved(store);
        await using var second = new CloudUploadCoordinator(store, http, fixture.AgentId, "PC-Test");
        second.SetHostAuthorized(true);
        second.Tick([fixture.Job]);
        await Until(() => second.NextReport()?.State == "Completed" || second.NextReport()?.State == "Failed");
        Check(second.NextReport()?.State == "Completed", "resumed upload completes after remote checksum verification");
        Check(google.ChunkOffsets.Count(x => x == 0) == 1 && google.ChunkOffsets.Contains(DriveUploader.ChunkSize), "retry resumes from server-confirmed bytes");
        Check(google.BinaryFiles == 1 && google.SessionsStarted == 1, "restart does not create another cloud file");
        Check(google.Refreshes >= 2, "rejected/expired access token is refreshed");
        var report = second.NextReport()!;
        second.Acknowledge(report.JobId, report.Sequence);
        Check(second.NextReport() is null, "acknowledged cloud result leaves the outbox");
    });

    await Run("lost completion response and authorization pause/reconnect", async () =>
    {
        var fixture = await Backup("lost-final");
        var store = Store("lost-final-store");
        Seed(store);
        var google = new FakeGoogle { LoseFinalResponse = true };
        using var http = new HttpClient(google);
        await using (var upload = new CloudUploadCoordinator(store, http, fixture.AgentId, "PC-Test"))
        {
            upload.SetHostAuthorized(true);
            upload.Tick([fixture.Job]);
            await Until(() => upload.NextReport()?.State == "Retrying" && !upload.IsBusy);
        }
        var requests = google.DataRequests;
        CloudUploadCoordinator.RetrySaved(store);
        await using (var resumed = new CloudUploadCoordinator(store, http, fixture.AgentId, "PC-Test"))
        {
            resumed.SetHostAuthorized(true);
            resumed.Tick([fixture.Job]);
            await Until(() => resumed.NextReport()?.State == "Completed");
        }
        Check(google.BinaryFiles == 1 && google.DataRequests == requests && google.SessionsStarted == 1,
            "lost final acknowledgment verifies the existing cloud file without re-uploading bytes");
        var authStore = Store("reauthorization-store");
        Seed(authStore);
        var rejected = new FakeGoogle { RejectRefresh = true };
        using var authHttp = new HttpClient(rejected);
        await using (var paused = new CloudUploadCoordinator(authStore, authHttp, fixture.AgentId, "PC-Test"))
        {
            paused.SetHostAuthorized(true);
            paused.Tick([fixture.Job]);
            await Until(() => paused.NextReport()?.State == "Needs sign-in" && !paused.IsBusy);
            var refreshes = rejected.Refreshes;
            paused.Tick([fixture.Job]);
            await Task.Delay(60);
            Check(rejected.Refreshes == refreshes, "rejected authorization stays paused instead of repeatedly refreshing");
        }
        Seed(authStore); // A completed browser sign-in produces a new grant generation.
        rejected.RejectRefresh = false;
        CloudUploadCoordinator.RetrySaved(authStore);
        await using var authorized = new CloudUploadCoordinator(authStore, authHttp, fixture.AgentId, "PC-Test");
        authorized.SetHostAuthorized(true);
        authorized.Tick([fixture.Job]);
        await Until(() => authorized.NextReport()?.State == "Completed");
        Check(File.Exists(fixture.Path) && fixture.Job.Report.State == "Completed", "new authorization resumes cloud work using the original local archive");
    });

    await Run("pending uploads never switch to a different Google account", async () =>
    {
        var fixture = await Backup("account-binding");
        var store = Store("account-binding-store");
        Seed(store);
        var google = new FakeGoogle { LoseFirstChunkResponse = true };
        using var http = new HttpClient(google);
        await using (var original = new CloudUploadCoordinator(store, http, fixture.AgentId, "PC-Test"))
        {
            original.SetHostAuthorized(true);
            original.Tick([fixture.Job]);
            await Until(() => original.NextReport()?.State == "Retrying" && !original.IsBusy);
        }
        var requests = google.DataRequests;
        store.Save("drive-account.dat", new DriveAccount { ClientId = "test.apps.googleusercontent.com", ClientSecret = "CLIENT_SECRET_SENTINEL",
            RefreshToken = "OTHER_REFRESH", Email = "other@example.invalid", UserId = "user-two" });
        await using var changed = new CloudUploadCoordinator(store, http, fixture.AgentId, "PC-Test");
        changed.SetHostAuthorized(true);
        changed.Tick([fixture.Job]);
        Check(changed.NextReport()?.State == "Needs sign-in" && !changed.IsBusy && google.DataRequests == requests,
            "changing accounts cannot send an old queued archive to the new account");
    });

    await Run("expired sessions, quota errors, checksum failure, and endpoint validation", async () =>
    {
        var fixture = await Backup("errors");
        var store = Store("expired-store");
        Seed(store);
        var google = new FakeGoogle { LoseFirstChunkResponse = true, ExpireNextStatus = true };
        using var http = new HttpClient(google);
        await using var uploads = new CloudUploadCoordinator(store, http, fixture.AgentId, "PC-Test");
        uploads.SetHostAuthorized(true);
        uploads.Tick([fixture.Job]);
        await Until(() => uploads.NextReport()?.State == "Retrying" && !uploads.IsBusy);
        // Force retries immediately for a deterministic test; production uses persisted backoff.
        await uploads.DisposeAsync();
        CloudUploadCoordinator.RetrySaved(store);
        await using var restart = new CloudUploadCoordinator(store, http, fixture.AgentId, "PC-Test");
        restart.SetHostAuthorized(true);
        restart.Tick([fixture.Job]);
        await Until(() => restart.NextReport()?.State == "Retrying" && !restart.IsBusy);
        await restart.DisposeAsync();
        CloudUploadCoordinator.RetrySaved(store);
        await using var third = new CloudUploadCoordinator(store, http, fixture.AgentId, "PC-Test");
        third.SetHostAuthorized(true);
        third.Tick([fixture.Job]);
        await Until(() => third.NextReport()?.State == "Completed" || third.NextReport()?.State == "Failed");
        Check(third.NextReport()?.State == "Completed" && google.UploadIds.Distinct().Count() == 1 && google.SessionsStarted == 2,
            "expired session restarts with the same preallocated file ID");

        foreach (var scenario in new[] { "checksum", "quota", "redirect", "rate-limit" })
        {
            var state = Store(scenario + "-store");
            Seed(state);
            var handler = new FakeGoogle { BadChecksum = scenario == "checksum", FullStorage = scenario == "quota", EvilLocation = scenario == "redirect", RateLimitOnce = scenario == "rate-limit" };
            using var transport = new HttpClient(handler);
            await using var upload = new CloudUploadCoordinator(state, transport, fixture.AgentId, "PC-Test");
            upload.SetHostAuthorized(true);
            upload.Tick([fixture.Job]);
            await Until(() => upload.NextReport()?.State is "Failed" or "Retrying" && !upload.IsBusy);
            Check(upload.NextReport()?.State == (scenario == "rate-limit" ? "Retrying" : "Failed"), scenario + " does not produce cloud success");
            Check(File.Exists(fixture.Path) && fixture.Job.Report.State == "Completed", scenario + " preserves local success");
            if (scenario == "redirect") Check(handler.DataRequests == 0, "archive bytes never go to an untrusted upload endpoint");
        }
        Check(!DriveHttp.SafeSessionUri("https://www.googleapis.com.evil.invalid/upload/drive/v3/files") &&
            !DriveHttp.SafeSessionUri("http://www.googleapis.com/upload/drive/v3/files"), "saved upload endpoint is restricted to HTTPS Google Drive");
    });

    await Run("authenticated host tracks local success independently from cloud failure and completion", async () =>
    {
        await using var host = new HostServer(Path.Combine(root, "host"), 0, IPAddress.Loopback);
        host.Start();
        var client = new AgentClient();
        var invitation = host.CreateInvitation("127.0.0.1");
        var identity = AgentClient.CreateCredentials(invitation, "Cloud-PC");
        await client.PairAsync(invitation, identity);
        var source = Path.Combine(root, "host-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "A real local backup, followed by cloud upload.");
        var plan = BackupPlan.Create(source, Path.Combine(root, "host-backups"));
        await client.PollAsync(identity, plan.Summary, null);
        var job = host.QueueBackup(identity.AgentId);
        var result = await new LocalBackupEngine().CreateAsync(plan, identity.AgentId, job.Command, _ => { });
        var local = new BackupReport(job.Command.JobId, 1, "Completed", 100, result.Bytes, result.Bytes, result.Files, result.Files, "Verified local backup.", result.ArchivePath);
        await client.PollAsync(identity, plan.Summary, local, cloudConfiguration: new("Connected", "backup@example.invalid", "Authorized."),
            cloudReport: new(job.Command.JobId, 1, "Uploading", 23, 23, 100, "Uploading directly from client."));
        Check(host.GetJobs().Single().Report.State == "Completed" && host.GetJobs().Single().Cloud?.State == "Uploading", "local complete and cloud uploading coexist");
        await client.PollAsync(identity, plan.Summary, null, cloudConfiguration: new("Needs sign-in", "backup@example.invalid", "Reconnect Google."),
            cloudReport: new(job.Command.JobId, 2, "Needs sign-in", 23, 23, 100, "Authorization was revoked."));
        Check(host.GetSnapshots().Single().LastSuccessfulJob is not null && host.GetJobs().Single().Report.State == "Completed", "cloud authorization failure does not change local result");
        var fileId = "verified-drive-file";
        await client.PollAsync(identity, plan.Summary, null, cloudConfiguration: new("Connected", "backup@example.invalid", "Authorized."),
            cloudReport: new(job.Command.JobId, 3, "Completed", 100, 100, 100, "Remote checksum verified.", fileId, $"https://drive.google.com/file/d/{fileId}/view"));
        Check(host.GetJobs().Single().Cloud?.State == "Completed" && host.GetJobs().Single().Report == local, "cloud retry completes without rerunning or rewriting local backup");
        var otherInvitation = host.CreateInvitation("127.0.0.1");
        var other = AgentClient.CreateCredentials(otherInvitation, "Other-PC");
        await client.PairAsync(otherInvitation, other);
        try { await client.PollAsync(other, null, null, cloudReport: new(job.Command.JobId, 99, "Failed", 0, 0, 0, "Foreign report")); throw new InvalidOperationException("A foreign agent reported another computer's upload."); }
        catch (AgentRejectedException) { }
        Check(host.GetJobs().Single().Cloud?.State == "Completed", "another paired agent cannot alter this cloud result");
    });
    Console.WriteLine("ALL DRIVE CHECKS PASSED (simulated Google HTTP; no live Google account used)");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
finally
{
    var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutoBack-drive-tests"));
    if (Directory.Exists(root) && Path.GetDirectoryName(root) == parent && Guid.TryParseExact(Path.GetFileName(root), "N", out _)) Directory.Delete(root, recursive: true);
}

ProtectedStore Store(string name) => new(Path.Combine(root, name));
static void Seed(ProtectedStore store) => store.Save("drive-account.dat", new DriveAccount
{ ClientId = "test.apps.googleusercontent.com", ClientSecret = "CLIENT_SECRET_SENTINEL", RefreshToken = "REFRESH_SENTINEL", Email = "backup@example.invalid", UserId = "user-one" });
async Task<(Guid AgentId, LocalJob Job, string Path)> Backup(string name)
{
    var source = Path.Combine(root, name, "source");
    Directory.CreateDirectory(source);
    await File.WriteAllBytesAsync(Path.Combine(source, "binary.bin"), RandomNumberGenerator.GetBytes(2 * DriveUploader.ChunkSize + 12345));
    var plan = BackupPlan.Create(source, Path.Combine(root, name, "archives"));
    var id = Guid.NewGuid();
    var command = new BackupCommand(Guid.NewGuid(), plan.PlanId, DateTimeOffset.UtcNow);
    var result = await new LocalBackupEngine().CreateAsync(plan, id, command, _ => { });
    return (id, new(command, new(command.JobId, 1, "Completed", 100, result.Bytes, result.Bytes, result.Files, result.Files, "Local backup verified.", result.ArchivePath)), result.ArchivePath);
}
static void Check(bool condition, string description)
{ if (!condition) throw new InvalidOperationException("FAIL: " + description); Console.WriteLine("  PASS " + description); }
static async Task Run(string name, Func<Task> run) { Console.WriteLine("CHECK " + name); await run(); Console.WriteLine("PASS " + name); }
static async Task Until(Func<bool> condition)
{
    var watch = Stopwatch.StartNew();
    while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Upload did not reach the expected state."); await Task.Delay(20); }
}
static async Task Reject(Func<Task> operation, DriveFailure failure)
{ try { await operation(); } catch (DriveException e) when (e.Failure == failure) { return; } throw new InvalidOperationException("Expected Drive failure was not reported."); }
