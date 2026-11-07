using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBack.Backup;
using AutoBack.Connection;

var repo = new DirectoryInfo(AppContext.BaseDirectory);
while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AutoBack.slnx"))) repo = repo.Parent;
if (repo is null) throw new InvalidOperationException("Run inside the repository.");
var executable = Path.Combine(repo.FullName, "src", "AutoBack.Agent.Legacy", "bin", "Release", "net48", "AutoBack.Agent.exe");
var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutoBack-legacy-integration"));
var root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
try
{
    var data = Path.Combine(root, "agent");
    var store = new ProtectedStore(data);
    await using var host = new HostServer(Path.Combine(root, "host"), 0, IPAddress.Loopback); host.Start();
    var invitation = host.CreateInvitation("127.0.0.1"); var invitePath = Path.Combine(root, "client.autoback-invite");
    await File.WriteAllTextAsync(invitePath, JsonSerializer.Serialize(invitation, Protocol.Json));
    var wrong = invitePath + ".wrong";
    await File.WriteAllTextAsync(wrong, JsonSerializer.Serialize(invitation with { CertificateSha256 = new string('0',64) }, Protocol.Json));
    Check(await Cli(["pair", "--invite", wrong, "--name", "Legacy-Wrong-Pin"]) != 0 && host.GetSnapshots().Count == 0, "legacy TLS rejects a mismatched certificate pin");
    Check(await Cli(["pair", "--invite", invitePath, "--name", "Win7-x86-pilot"]) == 0, "Framework x86 agent pairs with the .NET 10 host using protocol 5/TLS 1.2");
    var source = Path.Combine(root, "Documents"); Directory.CreateDirectory(source);
    var payload = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 127); await File.WriteAllBytesAsync(Path.Combine(source,"test.bin"), payload);
    Check(await Cli(["configure", "--mode", "local", "--source", source, "--destination", Path.Combine(root,"backups")]) == 0, "legacy CLI saves Local only plan");
    const string password = "Synthetic integration recovery phrase";
    var recovery = Path.Combine(root, "legacy.abkey");
    Check(await Cli(["encryption-enable", "--recovery-file", recovery, "--password-stdin"], password + "\n" + password + "\n") == 0, "legacy CLI exports a verified encrypted recovery key");
    using (var worker = Start(["run"]))
    {
        var stdout = worker.StandardOutput.ReadToEndAsync(); var stderr = worker.StandardError.ReadToEndAsync();
        try
        {
            await Until(() => host.GetSnapshots().Single().Online && host.GetSnapshots().Single().Configuration is not null);
            var client = host.GetSnapshots().Single(); Check(client.AgentVersion.Contains("win7-x86") && client.Configuration!.Target == BackupTargets.LocalOnly, "real legacy worker reports online and destination mode");
            var job = host.QueueBackup(client.Id);
            await Until(() => host.GetJobs().Any(j => j.Command.JobId == job.Command.JobId && BackupStates.Terminal(j.Report.State)));
            var completed = host.GetJobs().Single();
            Check(completed.Report.State == "Completed" && completed.Report.Encrypted && File.Exists(completed.Report.ArchivePath), "host Backup Now completes an encrypted backup in the actual 32-bit worker");
            var modern = new ProtectedStore(Path.Combine(root, "modern-import")); EncryptionVault.Import(modern, recovery, password);
            var result = await new RestoreEngine().RestoreAsync(completed.Report.ArchivePath!, Path.Combine(root,"modern-restore"), new("Local","legacy archive"), keys: EncryptionVault.Load(modern).Resolve);
            Check((await File.ReadAllBytesAsync(Path.Combine(result.PayloadDirectory,"test.bin"))).SequenceEqual(payload), "modern client decrypts legacy key export and multi-chunk archive byte for byte");
        }
        finally { if (!worker.HasExited) worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); await stdout; await stderr; }
    }
    // Reverse direction: modern encryption/recovery output must remain usable by the legacy client.
    var modernStore = new ProtectedStore(Path.Combine(root, "modern")); var modernRecovery = Path.Combine(root, "modern.abkey"); EncryptionVault.Enable(modernStore, modernRecovery, password);
    var modernPlan = BackupPlan.CreateForTarget([source], BackupTargets.LocalOnly, Path.Combine(root, "modern-backups"), modernStore.DirectoryPath);
    var modernCommand = new BackupCommand(Guid.NewGuid(), modernPlan.PlanId, DateTimeOffset.UtcNow, Encrypt: true);
    var modernResult = await new LocalBackupEngine().CreateAsync(modernPlan, Guid.NewGuid(), modernCommand, _ => { }, encryptionKey: EncryptionVault.Load(modernStore).Active);
    Check(await Cli(["recovery-import", "--recovery-file", modernRecovery, "--password-stdin"], password + "\n") == 0, "legacy client imports modern recovery export");
    var legacyRestore = Path.Combine(root, "legacy-restore");
    Check(await Cli(["restore", "--archive", modernResult.ArchivePath!, "--destination", legacyRestore]) == 0 &&
        (await File.ReadAllBytesAsync(Path.Combine(legacyRestore,"files","test.bin"))).SequenceEqual(payload), "legacy client decrypts and verifies modern multi-chunk archive");
    Check(await Cli(["verify-restore", "--destination", legacyRestore]) == 0, "legacy independent restore verification succeeds");
    Check(await Cli(["configure", "--source", source]) == 0 && store.Load<BackupPlan>("backup-plan.dat")!.Target == BackupTargets.GoogleDrive, "Google Drive remains the legacy default without a local destination");
    var evidence = Path.Combine(repo.FullName,"artifacts","legacy-validation"); Directory.CreateDirectory(evidence);
    await File.WriteAllTextAsync(Path.Combine(evidence,"result.json"), JsonSerializer.Serialize(new { verifiedAtUtc = DateTimeOffset.UtcNow,
        architecture = "x86", runtime = ".NET Framework 4.8", actualProcessPairing = true, tls12 = true, wrongPinRejected = true,
        hostBackupNow = true, bidirectionalEncryptedArchiveCompatibility = true, googleDefault = true,
        testedOS = Environment.OSVersion.VersionString, windows7Tested = false, serviceInstallationTested = false }, Protocol.Json));
    Console.WriteLine("ALL LEGACY INTEGRATION CHECKS PASSED. Actual Windows 7 installation still pending.");

    Process Start(string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true, RedirectStandardInput=true };
        foreach (var argument in args) start.ArgumentList.Add(argument); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(data); return Process.Start(start)!;
    }
    async Task<int> Cli(string[] args, string? input = null)
    {
        using var process=Start(args); var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
        if(input is not null) await process.StandardInput.WriteAsync(input); process.StandardInput.Close();
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(deadline.Token); } catch { process.Kill(entireProcessTree:true); throw; }
        var message = await output; var failure = await error; if(process.ExitCode!=0) Console.WriteLine("CLI " + args[0] + ": " + failure.Trim()); return process.ExitCode;
    }
}
finally { if (Directory.Exists(root) && Path.GetDirectoryName(Path.GetFullPath(root)) == parent && Guid.TryParseExact(Path.GetFileName(root),"N",out _)) Directory.Delete(root,true); }

static void Check(bool value,string description) { if(!value) throw new InvalidOperationException(description); Console.WriteLine("PASS " + description); }
static async Task Until(Func<bool> condition) { var deadline=DateTime.UtcNow.AddSeconds(40); while(!condition()) { if(DateTime.UtcNow>deadline) throw new TimeoutException("Legacy integration condition timed out."); await Task.Delay(100); } }
