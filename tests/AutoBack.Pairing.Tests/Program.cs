using System.Diagnostics;
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
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutoBack-pair-again"));
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Application.EnableVisualStyles();
            using (var form = new PairingSetupForm("Accounting", "host", "Host rejected this pairing") { Location = new(-16000,-16000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false })
            { form.Show(); Application.DoEvents(); form.PairAgainButton.PerformClick(); Check(form.SelectedAction == "pair-again" && form.DialogResult == DialogResult.OK, "Configure offers an explicit Pair again action"); }
            using (var form = new PairingSetupForm("Accounting", "host", "Connected") { Location = new(-16000,-16000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false })
            { form.Show(); Application.DoEvents(); form.FoldersButton.PerformClick(); Check(form.SelectedAction == "folders", "existing folder configuration remains a separate choice"); }
            SynchronizationContext.SetSynchronizationContext(null);
            RunAsync(root).GetAwaiter().GetResult(); Console.WriteLine("ALL PAIRING RECOVERY CHECKS PASSED (modern and x86 Framework processes; no service install)."); return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if(Directory.Exists(root) && Path.GetDirectoryName(Path.GetFullPath(root))==parent && Guid.TryParseExact(Path.GetFileName(root),"N",out _)) Directory.Delete(root,true); }
    }
    private static async Task RunAsync(string root)
    {
        var repo=new DirectoryInfo(AppContext.BaseDirectory); while(repo is not null && !File.Exists(Path.Combine(repo.FullName,"AutoBack.slnx"))) repo=repo.Parent;
        foreach(var edition in new[]{"modern","legacy"})
        {
            Console.WriteLine("CHECK " + edition + " revoked pairing recovery");
            var executable=Path.Combine(repo!.FullName,"src",edition=="modern"?"AutoBack.Agent":"AutoBack.Agent.Legacy","bin","Release",edition=="modern"?"net10.0-windows":"net48","AutoBack.Agent.exe");
            var data=Path.Combine(root,edition,"agent"); var store=new ProtectedStore(data);
            await using var host=new HostServer(Path.Combine(root,edition,"host"),0,IPAddress.Loopback); host.Start();
            var invitationPath=Path.Combine(root,edition,"invite.autoback-invite");
            await Invite(); Check(await Cli(["pair","--invite",invitationPath,"--name","Accounting"])==0,"initial pairing succeeds");
            var old=store.Load<AgentCredentials>("agent.dat")!;
            var source=Path.Combine(root,edition,"source"); Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source,"notes.txt"),"Preserve these source bytes.");
            Check(await Cli(["configure","--mode","local","--source",source,"--destination",Path.Combine(root,edition,"backups")])==0,"client configured before accidental removal");
            var plan=store.Load<BackupPlan>("backup-plan.dat")!;
            var key=new ArchiveKey{Id=Guid.NewGuid(),Material=Protocol.NewSecret()}; store.Save("encryption.dat",new EncryptionVault{Keys=[key],ActiveKeyId=key.Id,Enabled=true});
            store.Save("drive-account.dat",new DriveAccount{ClientId="test.apps.googleusercontent.com",ClientSecret="SYNTHETIC",RefreshToken="SYNTHETIC",Email="test@example.invalid",UserId="one"});
            store.Save("retention.dat",new RetentionPolicy(2,2));
            var oldJob=await Backup(old.AgentId);
            var oldPath=oldJob.Report.ArchivePath!; var oldBytes=await File.ReadAllBytesAsync(oldPath);
            var jobs=store.Load<JobJournal>(BackupCoordinator.JournalFile)!;
            var unfinished=new LocalJob(new(Guid.NewGuid(),plan.PlanId,DateTimeOffset.UtcNow),new(Guid.NewGuid(),1,"Copying",0,0,1,0,1,"Interrupted old work"));
            unfinished=unfinished with{Report=unfinished.Report with{JobId=unfinished.Command.JobId}};
            store.Save(BackupCoordinator.JournalFile,new JobJournal([..jobs.Jobs.Select(j=>j with{Acknowledged=false}),unfinished]));
            var accountKey=store.Load<DriveAccount>("drive-account.dat")!.Key;
            store.Save("drive-folders.dat",new Dictionary<string,DriveFolders>{{accountKey,new("old-root","old-client")}});
            store.Save("drive-uploads.dat",new UploadJournal([new(oldJob.Command.JobId,oldPath,accountKey,new(FileId:"old-file"),new(oldJob.Command.JobId,3,"Retrying",0,0,oldBytes.Length,"Saved old cloud work"),DateTimeOffset.UtcNow)]));
            var kept=new[]{"backup-plan.dat","encryption.dat","drive-account.dat","retention.dat"}.ToDictionary(n=>n,n=>File.ReadAllBytes(Path.Combine(data,n)));
            host.Revoke(old.AgentId);
            var invalid=host.CreateInvitation("127.0.0.1") with{Secret=new string('0',64)}; await File.WriteAllTextAsync(invitationPath,JsonSerializer.Serialize(invalid,Protocol.Json));
            Check(await Cli(["re-pair","--invite",invitationPath])!=0 && store.Load<AgentCredentials>("agent.dat")==old && store.Load<JobJournal>(BackupCoordinator.JournalFile)!.Jobs.Count==2,"invalid invitation preserves identity and old journals");
            await Invite();
            Check(await Cli(["setup","--replace","--invite",invitationPath])==0,"Configure recovery route accepts a fresh invitation without opening folder or Google setup");
            var next=store.Load<AgentCredentials>("agent.dat")!;
            Check(next.AgentId!=old.AgentId && next.Name==old.Name && host.GetSnapshots().Single().Id==next.AgentId,"fresh pairing preserves the client alias and replaces the revoked identity");
            var archiveFile=$"pairing-history-{old.AgentId:N}-{next.AgentId:N}.dat";
            var archive=store.Load<PairingArchive>(archiveFile)!;
            Check(archive.Jobs.Jobs.Count==2 && archive.Uploads.Uploads.Single().Checkpoint.FileId=="old-file" && archive.Folders![accountKey].ClientId=="old-client","old jobs, upload checkpoints and Drive folders are archived in DPAPI storage");
            Check(store.Load<JobJournal>(BackupCoordinator.JournalFile)!.Jobs.Count==0 && store.Load<JobJournal>("jobs.dat")!.Jobs.Count==0 && store.Load<UploadJournal>("drive-uploads.dat")!.Uploads.Count==0 && store.Load<Dictionary<string,DriveFolders>>("drive-folders.dat")!.Count==0,"retired work cannot replay and old Drive folders cannot be reused by the new identity");
            Check(kept.All(p=>File.ReadAllBytes(Path.Combine(data,p.Key)).SequenceEqual(p.Value)) && (await File.ReadAllBytesAsync(oldPath)).SequenceEqual(oldBytes),"folders, Google credentials, encryption keys, retention and old archive remain unchanged");
            Check(await Cli(["pair","--invite",invitationPath])!=0,"ordinary pairing still refuses accidental replacement");
            var newJob=await Backup(next.AgentId);
            Check(newJob.Report.Encrypted && newJob.Report.State=="Completed","new worker connects and backs up successfully despite retired unacknowledged reports");
            var recovered=await new RestoreEngine().RestoreAsync(oldPath,Path.Combine(root,edition,"restore-old"),new("Local","old pairing archive"),keys:EncryptionVault.Load(store).Resolve);
            Check(await File.ReadAllTextAsync(Path.Combine(recovered.PayloadDirectory,"notes.txt"))=="Preserve these source bytes.","backups from the old pairing remain decryptable and usable");
            using(var google=new FakeGoogle())
            using(var http=new HttpClient(google))
            {
                var result=await new DriveUploader(store,new GoogleTokenProvider(store,http),http,next.AgentId,next.Name).UploadAsync(newJob.Command.JobId,newJob.Report.ArchivePath!,accountKey,new(),_=>{},_=>{},CancellationToken.None);
                Check(!string.IsNullOrEmpty(result.FileId)&&google.BinaryFiles==1&&store.Load<Dictionary<string,DriveFolders>>("drive-folders.dat")![accountKey].ClientId!="old-client","preserved Google grant uploads into a fresh identity-owned Drive folder without repeating consent (HTTP simulated)");
            }
            var beforeMalformed=File.ReadAllBytes(Path.Combine(data,BackupCoordinator.JournalFile));
            store.Save("pairing-transition.dat",new{Credentials=next,ArchiveFile="invalid.dat",Archive=new PairingArchive(old.AgentId,old.Name,old.Host,DateTimeOffset.UtcNow,new([]),new([]))});
            Check(await Cli(["status"])!=0&&File.ReadAllBytes(Path.Combine(data,BackupCoordinator.JournalFile)).SequenceEqual(beforeMalformed),"malformed transition fails without clearing current jobs");
            store.Delete("pairing-transition.dat");
            // Model interruption after the host accepted a new credential but before all local files were reset.
            var originalJournal=store.Load<JobJournal>(BackupCoordinator.JournalFile)!;
            var interruptedArchive=new PairingArchive(old.AgentId,old.Name,old.Host,DateTimeOffset.UtcNow,originalJournal,new([]));
            store.Save("pairing-transition.dat",new{Credentials=next,ArchiveFile=archiveFile,Archive=interruptedArchive});
            Check(await Cli(["status"])==0 && store.Load<JobJournal>(BackupCoordinator.JournalFile)!.Jobs.Count==0 && !File.Exists(Path.Combine(data,"pairing-transition.dat")),"restart finishes an interrupted journal transition before starting a worker");
            async Task Invite() => await File.WriteAllTextAsync(invitationPath,JsonSerializer.Serialize(host.CreateInvitation("127.0.0.1"),Protocol.Json));
            Process Start(string[] args) { var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)start.ArgumentList.Add(arg); start.ArgumentList.Add("--data-dir");start.ArgumentList.Add(data);return Process.Start(start)!; }
            async Task<int> Cli(string[] args) { using var process=Start(args);var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));try{await process.WaitForExitAsync(deadline.Token);}catch{process.Kill(entireProcessTree:true);throw;}await output;await error;return process.ExitCode; }
            async Task<HostBackupJob> Backup(Guid id)
            {
                using var process=Start(["run"]);var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
                try{await Until(()=>host.GetSnapshots().Any(a=>a.Id==id&&a.Online&&a.Configuration is not null));var job=host.QueueBackup(id);await Until(()=>host.GetJobs().Any(j=>j.Command.JobId==job.Command.JobId&&BackupStates.Terminal(j.Report.State)));return host.GetJobs().Single(j=>j.Command.JobId==job.Command.JobId);}
                finally{if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync();await output;await error;}
            }
        }
        var evidence=Path.Combine(repo!.FullName,"artifacts","pairing-recovery-validation");Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence,"result.json"),JsonSerializer.Serialize(new{verifiedAtUtc=DateTimeOffset.UtcNow,modernProcess=true,legacyX86Process=true,revokedPairingRecovered=true,oldDataPreserved=true,pendingWorkArchived=true,restartRecovery=true,serviceInstallationTested=false},Protocol.Json));
    }
    private static void Check(bool value,string detail){if(!value)throw new InvalidOperationException(detail);Console.WriteLine("PASS "+detail);}
    private static async Task Until(Func<bool> condition){var deadline=DateTime.UtcNow.AddSeconds(30);while(!condition()){if(DateTime.UtcNow>deadline)throw new TimeoutException("Pairing recovery check timed out.");await Task.Delay(100);}}
}
