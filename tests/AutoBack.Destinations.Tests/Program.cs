using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Security.Cryptography;
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
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutoBack-destination-tests"));
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AutoBack.slnx"))) repo = repo.Parent;
        try
        {
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "document.txt"), "Synthetic destination mode test data.");
            using (var form = new BackupFoldersForm(dataDirectory: Path.Combine(root, "ui-data")) { StartPosition = FormStartPosition.Manual, Location = new(-16000,-16000), ShowInTaskbar = false })
            {
                form.Show(); Application.DoEvents(); form.AddFolder(source);
                Check(form.GoogleDriveMode.Checked && !form.DestinationInput.Enabled && form.SaveButton.Enabled, "new setup defaults to Google Drive without a permanent destination field");
                Check(form.TryBuildPlan(out var cloud) && cloud!.Target == BackupTargets.GoogleDrive && cloud.Destination == BackupPlan.CacheDirectory(Path.Combine(root, "ui-data")), "Google Drive uses an automatic private cache");
                using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new(0,0,form.Width,form.Height)); bitmap.Save(Path.Combine(repo!.FullName, "docs", "google-drive-mode-preview.png")); }
                form.LocalOnlyMode.Checked = true;
                Check(form.DestinationInput.Enabled && !form.SaveButton.Enabled, "Local only requires a permanent destination");
                form.DestinationInput.Text = Path.Combine(root, "local-backups");
                Check(form.TryBuildPlan(out var local) && local!.Target == BackupTargets.LocalOnly, "Local only is an explicit selectable mode");
                form.GoogleDriveMode.Checked = true;
                Check(form.TryBuildPlan(out cloud) && cloud!.Destination != form.DestinationInput.Text, "switching back to Google Drive ignores the old local path");
                form.Close();
            }
            SynchronizationContext.SetSynchronizationContext(null);
            RunAsync(root, source, repo!).GetAwaiter().GetResult(); Console.WriteLine("ALL DESTINATION MODE CHECKS PASSED"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            if (Directory.Exists(root) && Path.GetDirectoryName(Path.GetFullPath(root)) == parent && Guid.TryParseExact(Path.GetFileName(root), "N", out _)) Directory.Delete(root, true);
        }
    }

    private static async Task RunAsync(string root, string source, DirectoryInfo repo)
    {
        var agentId = Guid.NewGuid();
        Console.WriteLine("CHECK Google Drive preflight and local-only upload isolation");
        var unavailable = Store("unavailable"); var cloudPlan = BackupPlan.CreateForTarget([source], BackupTargets.GoogleDrive, null, unavailable.DirectoryPath); unavailable.Save("backup-plan.dat", cloudPlan);
        await using (var backups = new BackupCoordinator(unavailable, agentId))
        {
            backups.Handle(new(Guid.NewGuid(), cloudPlan.PlanId, DateTimeOffset.UtcNow)); await Until(() => backups.NextReport()?.State == "Failed");
            Check(!Directory.Exists(cloudPlan.Destination) && backups.NextReport()!.Target == BackupTargets.GoogleDrive, "missing Google sign-in fails before creating a temporary archive");
        }
        var localStore = Store("local"); Seed(localStore);
        var localPlan = BackupPlan.CreateForTarget([source], BackupTargets.LocalOnly, Path.Combine(root, "permanent"), localStore.DirectoryPath); localStore.Save("backup-plan.dat", localPlan);
        await using (var backups = new BackupCoordinator(localStore, agentId, () => throw new InvalidOperationException("Local mode must not request a cloud account")))
        {
            backups.Handle(new(Guid.NewGuid(), localPlan.PlanId, DateTimeOffset.UtcNow)); await Until(() => backups.NextReport()?.State == "Completed");
            using var google = new FakeGoogle(); using var http = new HttpClient(google);
            await using var uploads = new CloudUploadCoordinator(localStore, http, agentId, "Local-Only"); uploads.SetHostAuthorized(true); uploads.Tick(backups.CompletedJobs());
            Check(uploads.AllUploads().Count == 0 && google.Refreshes == 0 && google.DataRequests == 0 && File.Exists(backups.NextReport()!.ArchivePath), "local-only archives remain local even with a connected Google account");
            var snapshot = backups.CompletedJobs().Single();
            uploads.Tick([snapshot with { Target = BackupTargets.LegacyBoth }]);
            Check(uploads.AllUploads().Count == 0, "a local-only report also blocks accidental rediscovery as a legacy upload");
            localStore.Save("drive-uploads.dat", new UploadJournal([new(Guid.NewGuid(), "not-read.zip", new GoogleTokenProvider(localStore,http).Account!.Key, new(),
                new(Guid.NewGuid(),1,"Queued",0,0,0,"Old pending work"), DateTimeOffset.UtcNow)]));
        }
        using (var google = new FakeGoogle())
        using (var http = new HttpClient(google))
        await using (var paused = new CloudUploadCoordinator(localStore, http, agentId, "Local-Only"))
        {
            paused.SetHostAuthorized(true); paused.SetEnabled(false); paused.Tick([]);
            Check(google.Refreshes == 0 && paused.AllUploads().Single().Report.State == "Queued", "local-only mode pauses older pending cloud work without deleting its journal");
        }

        Console.WriteLine("CHECK verified cloud cleanup, failed upload preservation, safety and restart");
        var store = Store("cloud"); Seed(store); var accountKey = store.Load<DriveAccount>("drive-account.dat")!.Key;
        var plan = BackupPlan.CreateForTarget([source], BackupTargets.GoogleDrive, null, store.DirectoryPath); store.Save("backup-plan.dat", plan);
        await using (var backups = new BackupCoordinator(store, agentId, () => accountKey))
        {
            await using var cloudHost = new HostServer(Path.Combine(root,"cleanup-host"),0,IPAddress.Loopback); cloudHost.Start();
            var invitation = cloudHost.CreateInvitation("127.0.0.1"); var cloudIdentity = AgentClient.CreateCredentials(invitation,"Cleanup-PC") with { AgentId = agentId }; var cloudClient = new AgentClient();
            await cloudClient.PairAsync(invitation,cloudIdentity); await cloudClient.PollAsync(cloudIdentity,plan.Summary,null,cloudConfiguration:new("Connected","test@example.invalid","Authorized"));
            var command = cloudHost.QueueBackup(agentId).Command; backups.Handle(command); await Until(() => backups.NextReport()?.State == "Completed");
            var local = backups.AllJobs().Single(); var path = local.Report.ArchivePath!; var bytes = await File.ReadAllBytesAsync(path);
            var prepared = await cloudClient.PollAsync(cloudIdentity,plan.Summary,local.Report); backups.Acknowledge(prepared.AcknowledgedJobId,prepared.AcknowledgedSequence);
            Check(cloudHost.GetSnapshots().Single().LastSuccessfulJob is null, "preparing a cloud archive alone is not reported as a confirmed backup");
            using var google = new FakeGoogle(); using var http = new HttpClient(google);
            await using var uploads = new CloudUploadCoordinator(store, http, agentId, "Cloud-Only"); uploads.SetHostAuthorized(true);
            var cleanup = new CloudCacheCleanup(store,http,agentId);
            Check(await cleanup.RunAsync(backups.AllJobs(), uploads.AllUploads(), backups.MarkTemporaryRemoved) == 0 && File.Exists(path), "unuploaded temporary archive is preserved");
            uploads.Tick(backups.CompletedJobs()); await Until(() => uploads.AllUploads().Any(u => u.Report.State == "Completed")); await Until(() => !uploads.IsBusy);
            Check(await cleanup.RunAsync(backups.AllJobs(), uploads.AllUploads(), backups.MarkTemporaryRemoved) == 0 && File.Exists(path), "remote completion without host acknowledgment does not clean the cache");
            var report = uploads.AllUploads().Single().Report;
            var confirmed = await cloudClient.PollAsync(cloudIdentity,plan.Summary,null,cloudReport:report); uploads.Acknowledge(confirmed.AcknowledgedCloudJobId,confirmed.AcknowledgedCloudSequence);
            Check(cloudHost.GetSnapshots().Single().LastSuccessfulJob?.Cloud?.State == "Completed", "host confirms Google Drive backup only after verified upload");
            google.PretendFilesTrashed = true; await Reject(() => cleanup.RunAsync(backups.AllJobs(),uploads.AllUploads(),backups.MarkTemporaryRemoved)); google.PretendFilesTrashed = false;
            google.TamperRetentionMetadata = true; await Reject(() => cleanup.RunAsync(backups.AllJobs(),uploads.AllUploads(),backups.MarkTemporaryRemoved)); google.TamperRetentionMetadata = false;
            Check(File.Exists(path), "missing/trashed or changed remote metadata preserves the temporary archive");
            var changed = bytes.ToArray(); changed[0] ^= 1; await File.WriteAllBytesAsync(path, changed);
            await Reject(() => cleanup.RunAsync(backups.AllJobs(),uploads.AllUploads(),backups.MarkTemporaryRemoved)); await File.WriteAllBytesAsync(path,bytes);
            var unrelated = Path.Combine(root,"unrelated.zip"); await File.WriteAllTextAsync(unrelated,"KEEP");
            await Reject(() => cleanup.RunAsync([backups.AllJobs().Single() with { Report = local.Report with { ArchivePath = unrelated }, Acknowledged = true }],uploads.AllUploads(),backups.MarkTemporaryRemoved));
            Check(await File.ReadAllTextAsync(unrelated) == "KEEP", "cleanup refuses paths outside its exact managed archive");
            await Reject(() => cleanup.RunAsync(backups.AllJobs(),uploads.AllUploads(),_ => throw new IOException("Simulate a crash after deletion before annotation")));
            Check(!File.Exists(path) && !backups.AllJobs().Single().Report.TemporaryArchiveRemoved, "interrupted annotation remains distinguishable from a confirmed journal update");
            Check(await cleanup.RunAsync(backups.AllJobs(),uploads.AllUploads(),backups.MarkTemporaryRemoved) == 1 && backups.AllJobs().Single().Report.TemporaryArchiveRemoved, "restart finishes cleanup annotation without creating another upload");
            await cloudClient.PollAsync(cloudIdentity,plan.Summary,backups.NextReport());
            Check(cloudHost.GetJobs().Single().Report.TemporaryArchiveRemoved, "host accepts the authenticated temporary-cleanup result without rewriting backup success");
            Check(google.BinaryFiles == 1 && File.ReadAllText(Path.Combine(source,"document.txt")).Contains("Synthetic"), "only the owned temporary archive is removed; source and verified cloud file remain");
        }
        // A transfer failure must keep its archive and stable resumable upload identity for retry.
        var retryStore = Store("retry"); Seed(retryStore); var retryPlan = BackupPlan.CreateForTarget([source],BackupTargets.GoogleDrive,null,retryStore.DirectoryPath); retryStore.Save("backup-plan.dat",retryPlan);
        await using (var backups = new BackupCoordinator(retryStore, agentId, () => retryStore.Load<DriveAccount>("drive-account.dat")!.Key))
        {
            backups.Handle(new(Guid.NewGuid(),retryPlan.PlanId,DateTimeOffset.UtcNow)); await Until(() => backups.NextReport()?.State == "Completed");
            var local = backups.AllJobs().Single(); backups.Acknowledge(local.Report.JobId,local.Report.Sequence);
            using var google = new FakeGoogle { LoseFinalResponse = true }; using var http = new HttpClient(google);
            await using (var uploads = new CloudUploadCoordinator(retryStore,http,agentId,"Retry"))
            {
                uploads.SetHostAuthorized(true); uploads.Tick(backups.CompletedJobs()); await Until(() => uploads.AllUploads().Single().Report.State == "Retrying" && !uploads.IsBusy);
                Check(await new CloudCacheCleanup(retryStore,http,agentId).RunAsync(backups.AllJobs(),uploads.AllUploads(),backups.MarkTemporaryRemoved) == 0 && File.Exists(local.Report.ArchivePath), "failed upload keeps its temporary archive for retry");
            }
            CloudUploadCoordinator.RetrySaved(retryStore);
            await using var resumed = new CloudUploadCoordinator(retryStore,http,agentId,"Retry"); resumed.SetHostAuthorized(true); resumed.Tick(backups.CompletedJobs());
            await Until(() => resumed.AllUploads().Single().Report.State == "Completed" && !resumed.IsBusy);
            var report = resumed.AllUploads().Single().Report; resumed.Acknowledge(report.JobId,report.Sequence);
            Check(await new CloudCacheCleanup(retryStore,http,agentId).RunAsync(backups.AllJobs(),resumed.AllUploads(),backups.MarkTemporaryRemoved) == 1 && google.BinaryFiles == 1, "retry verifies the original cloud file then cleans the cache once");
        }

        Console.WriteLine("CHECK old-journal migration and real host/client mode changes");
        var migration = Store("migration"); var legacyJob = new LocalJob(new(Guid.NewGuid(), new string('A',64),DateTimeOffset.UtcNow), new(Guid.NewGuid(),1,"Failed",0,0,0,0,0,"Legacy failure"));
        migration.Save("jobs.dat",new JobJournal([legacyJob])); var oldBytes = await File.ReadAllBytesAsync(Path.Combine(migration.DirectoryPath,"jobs.dat"));
        await using (var backups = new BackupCoordinator(migration,agentId)) Check(backups.AllJobs().Count == 1 && File.Exists(Path.Combine(migration.DirectoryPath,BackupCoordinator.JournalFile)), "legacy jobs migrate into the new journal without losing history");
        Check((await File.ReadAllBytesAsync(Path.Combine(migration.DirectoryPath,"jobs.dat"))).SequenceEqual(oldBytes), "old journal stays untouched so old agents cannot rediscover new local-only jobs");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var executable = Path.Combine(repo.FullName,"src","AutoBack.Agent","bin",configuration,"net10.0-windows","AutoBack.Agent.exe");
        var data = Path.Combine(root,"process"); var processStore = new ProtectedStore(data); var permanent = Path.Combine(root,"process-local");
        Check(await Cli(["configure","--mode","local","--source",source,"--destination",permanent]) == 0, "real CLI configures Local only explicitly");
        await using var host = new HostServer(Path.Combine(root,"host"),0,IPAddress.Loopback); host.Start(); var client = new AgentClient();
        var invite = host.CreateInvitation("127.0.0.1"); var identity = AgentClient.CreateCredentials(invite,"Modes-PC"); await client.PairAsync(invite,identity); processStore.Save("agent.dat",identity);
        string? localPath = null;
        using (var worker = Start(["run"]))
        {
            var stdout = worker.StandardOutput.ReadToEndAsync(); var stderr = worker.StandardError.ReadToEndAsync();
            try
            {
                await Until(() => host.GetSnapshots().Single().Configuration?.Target == BackupTargets.LocalOnly); host.QueueBackup(identity.AgentId);
                await Until(() => host.GetJobs().Single().Report.State == "Completed"); localPath = host.GetJobs().Single().Report.ArchivePath;
                Check(File.Exists(localPath) && host.GetJobs().Single().Cloud is null, "actual local-only backup completes without a cloud job");
                Check(await Cli(["configure","--source",source]) != 0 && processStore.Load<BackupPlan>("backup-plan.dat")!.Target == BackupTargets.LocalOnly, "configuration mode cannot change underneath a running agent");
                await Reject(() => client.PollAsync(identity,processStore.Load<BackupPlan>("backup-plan.dat")!.Summary,null,cloudReport:new(host.GetJobs().Single().Command.JobId,1,"Completed",100,1,1,"Must reject","file","https://drive.google.com/file/d/file/view")));
            }
            finally { if (!worker.HasExited) worker.Kill(entireProcessTree:true); await worker.WaitForExitAsync(); await stdout; await stderr; }
        }
        Check(await Cli(["configure","--source",source]) == 0 && processStore.Load<BackupPlan>("backup-plan.dat")!.Target == BackupTargets.GoogleDrive, "CLI without --destination defaults to Google Drive");
        using (var worker = Start(["run"]))
        {
            var stdout = worker.StandardOutput.ReadToEndAsync(); var stderr = worker.StandardError.ReadToEndAsync();
            try
            {
                await Until(() => host.GetSnapshots().Single().Configuration?.Target == BackupTargets.GoogleDrive); host.QueueBackup(identity.AgentId);
                await Until(() => host.GetJobs().Last().Report.State == "Failed");
                Check(File.Exists(localPath) && host.GetSnapshots().Single().LastSuccessfulJob!.Report.Target == BackupTargets.LocalOnly, "switching modes preserves old local archives and never counts unuploaded staging as a confirmed backup");
            }
            finally { if (!worker.HasExited) worker.Kill(entireProcessTree:true); await worker.WaitForExitAsync(); await stdout; await stderr; }
        }
        var evidence = Path.Combine(repo.FullName,"artifacts","destination-validation"); Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence,"result.json"),JsonSerializer.Serialize(new { verifiedAtUtc=DateTimeOffset.UtcNow, defaultGoogleDrive=true, localOnlyNoUpload=true,
            verifiedCleanup=true, failurePreservesCache=true, cleanupRestartSafe=true, oldBackupsPreserved=true, actualClientModes=true, googleHttp="Simulated" },Protocol.Json));

        ProtectedStore Store(string name) => new(Path.Combine(root,name));
        Process Start(string[] args)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
            foreach(var argument in args) start.ArgumentList.Add(argument); start.ArgumentList.Add("--data-dir");start.ArgumentList.Add(data); return Process.Start(start)!;
        }
        async Task<int> Cli(string[] args)
        {
            using var process=Start(args); var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(entireProcessTree:true);throw; } await output;await error;return process.ExitCode;
        }
    }
    private static void Seed(ProtectedStore store) => store.Save("drive-account.dat", new DriveAccount { ClientId="test.apps.googleusercontent.com",ClientSecret="TEST_CLIENT",RefreshToken="REFRESH_SENTINEL",UserId="user-one",Email="backup@example.invalid" });
    private static void Check(bool condition,string message) { if(!condition)throw new InvalidOperationException("FAIL: "+message);Console.WriteLine("  PASS "+message); }
    private static async Task Until(Func<bool> condition) { var watch=Stopwatch.StartNew();while(!condition()){if(watch.Elapsed>TimeSpan.FromSeconds(35))throw new TimeoutException("Destination mode operation timed out.");await Task.Delay(50);} }
    private static async Task Reject(Func<Task> action)
    {
        try {await action();}catch(Exception e)when(e is IOException or InvalidDataException or ArgumentException or AgentRejectedException){return;}
        throw new InvalidOperationException("Expected destination validation rejection did not occur.");
    }
}
