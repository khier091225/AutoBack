using System.Security.Cryptography;
using AutoBack.Backup;
using AutoBack.Connection;
using AutoBack.Drive;

namespace AutoBack.Drive.Tests;

internal static class TreeChecks
{
    public static async Task RunAsync(string root)
    {
        Console.WriteLine("CHECK incremental recovery, interrupted encrypted updates, ownership and history");
        var store = new ProtectedStore(Path.Combine(root, "encrypted-tree"));
        store.Save("drive-account.dat", new DriveAccount { ClientId="test.apps.googleusercontent.com",ClientSecret="SYNTHETIC",RefreshToken="SYNTHETIC",Email="test@example.invalid",UserId="user-one" });
        const string password = "Synthetic tree test passphrase";
        var recovery=Path.Combine(root,"tree.abkey"); EncryptionVault.Enable(store,recovery,password);
        var source=Path.Combine(root,"encrypted-source","Documents"); Directory.CreateDirectory(source);
        var payload=RandomNumberGenerator.GetBytes(2 * DriveUploader.ChunkSize + 51);
        await File.WriteAllBytesAsync(Path.Combine(source,"large.bin"),payload);
        var plan=BackupPlan.CreateForTarget([source],BackupTargets.GoogleDrive,null,store.DirectoryPath);
        var agent=Guid.NewGuid(); var account=store.Load<DriveAccount>("drive-account.dat")!.Key;
        var google=new FakeGoogle { LoseFirstChunkResponse=true }; using var http=new HttpClient(google);
        var uploader=new DriveTreeUploader(store,http,agent,"Same-Name");
        async Task<(BackupCommand Command,BackupResult Result)> Prepare()
        {
            var command=new BackupCommand(Guid.NewGuid(),plan.PlanId,DateTimeOffset.UtcNow,Encrypt:true);
            return (command,await new LocalBackupEngine().CreateAsync(plan,agent,command,_=>{},encryptionKey:EncryptionVault.Load(store).Active));
        }
        Task<TreeUploadResult> Upload((BackupCommand Command,BackupResult Result) backup) => uploader.UploadAsync(backup.Command.JobId,backup.Result.ArchivePath,account,_=>{},_=>{},CancellationToken.None);
        var first=await Prepare();
        try { await Upload(first); throw new Exception("Expected lost chunk response."); } catch (HttpRequestException) { }
        var saved=store.Load<Dictionary<string,TreeIndex>>(DriveTreeUploader.IndexFile)![account];
        Check(saved.Snapshot.Files.Single().Transfer.SessionUri is not null,"individual encrypted file checkpoints survive a lost response");
        var folderId=saved.Snapshot.FolderId;
        await Reject(()=>new DriveDownloader(store,http).DownloadAsync(folderId,Path.Combine(root,"unfinished.zip")));
        Check(!File.Exists(Path.Combine(root,"unfinished.zip")),"restore refuses an unfinished cloud folder");
        var resumed=await Upload(first);
        Check(google.ChunkOffsets.Count(offset=>offset==0)==2 && google.ChunkOffsets.Contains(DriveUploader.ChunkSize),"encrypted resume sends large file's first chunk once, then continues at the confirmed offset (plus recovery index)");
        var encryptedFile=google.Files.Single(f=>f.GetProperty("name").GetString()=="large.bin.abk");
        Check(!google.Bytes(encryptedFile.GetProperty("id").GetString()!).SequenceEqual(payload),"Drive holds encrypted per-file content when encryption is enabled");
        var separate=new ProtectedStore(Path.Combine(root,"separate-recovery"));
        separate.Save("drive-account.dat",store.Load<DriveAccount>("drive-account.dat")!); EncryptionVault.Import(separate,recovery,password);
        var downloaded=Path.Combine(root,"encrypted-tree-restored.zip"); await new DriveDownloader(separate,http).DownloadAsync(resumed.FolderId,downloaded);
        var restored=await new RestoreEngine().RestoreAsync(downloaded,Path.Combine(root,"encrypted-tree-restored"),new("GoogleDrive","encrypted folder"));
        Check((await File.ReadAllBytesAsync(Path.Combine(restored.PayloadDirectory,"Documents","large.bin"))).SequenceEqual(payload),"another store with the recovery export restores encrypted folder files byte for byte");
        // Updating the existing blob must also resume after a lost final response.
        payload[0]^=1; await File.WriteAllBytesAsync(Path.Combine(source,"large.bin"),payload);
        var second=await Prepare(); var fileId=encryptedFile.GetProperty("id").GetString();
        google.LoseFinalResponse=true;
        try { await Upload(second); throw new Exception("Expected lost final response."); } catch(HttpRequestException) { }
        var requests=google.DataRequests; await Upload(second);
        Check(google.DataRequests==requests+1 && google.Files.Single(f=>f.GetProperty("name").GetString()=="large.bin.abk").GetProperty("id").GetString()==fileId,"a lost update response verifies the same file without resending it; only the index remains");
        // Lost local folder mapping must discover this same installation, even with paginated roots.
        store.Delete("drive-folders.dat"); google.ListingPageSize=1;
        var third=await Upload(await Prepare()); Check(third.FolderId==folderId && third.UploadedFiles==0,"folder rediscovery reuses the installation's existing folder");
        // A same-name PC has a separate protected device identity and must never overwrite this one.
        var other=new ProtectedStore(Path.Combine(root,"other-same-name")); other.Save("drive-account.dat",store.Load<DriveAccount>("drive-account.dat")!);
        var otherApi=new DriveApi(http,new(other,http),account); var otherFolder=await new DriveFolderManager(other,otherApi,Guid.NewGuid(),"Same-Name").EnsureAsync(account,CancellationToken.None);
        Check(otherFolder.ClientId!=folderId,"same-name computers cannot adopt or overwrite each other's backup folder");
        var dir=store.Load<Dictionary<string,TreeIndex>>(DriveTreeUploader.IndexFile)![account].Snapshot.Directories.Single();
        google.Trash(dir.Id); var repaired=await Upload(await Prepare());
        Check(repaired.FolderId==folderId && repaired.UploadedFiles==1,"deleting a managed child folder recreates only that subtree inside the existing PC folder");
        var cache=store.Load<Dictionary<string,DriveFolders>>("drive-folders.dat")![account]; google.Delete(cache.RootId);
        // Permanent deletion in real Drive deletes descendants; the fixture marks them explicitly too.
        google.Delete(folderId);
        var rootRecovered=await Upload(await Prepare()); Check(rootRecovered.FolderId!=folderId,"permanently deleted root/PC folders allocate fresh IDs and recover automatically");
        payload[1]^=1; await File.WriteAllBytesAsync(Path.Combine(source,"large.bin"),payload);
        var retired=await Prepare(); google.LoseFirstChunkResponse=true;
        try { await Upload(retired); throw new Exception("Expected interrupted old identity transfer."); } catch(HttpRequestException) { }
        agent=Guid.NewGuid(); uploader=new(store,http,agent,"Same-Name");
        var pairedAgain=await Upload(await Prepare());
        Check(pairedAgain.FolderId==rootRecovered.FolderId,"a new pairing reuses the same installation folder and safely resumes identical current bytes without replaying retired jobs");
        var before=google.DataRequests;
        google.Alter(rootRecovered.FolderId,"appProperties",new Dictionary<string,string>{{"autobackDevice",Guid.NewGuid().ToString("N")},{"autobackAgent",Guid.NewGuid().ToString("N")}});
        await Reject(()=>UploadPrepared());
        Check(google.DataRequests==before,"unexpected cached folder ownership stops before sending file bytes");
        async Task UploadPrepared() => await Upload(await Prepare());
        Console.WriteLine("PASS incremental recovery, interrupted encrypted updates, ownership and history");
        await PipelineAsync(root);
    }

    private static async Task PipelineAsync(string root)
    {
        Console.WriteLine("CHECK rolling folder history, acknowledged cleanup and ordered retries over real TLS");
        var store=new ProtectedStore(Path.Combine(root,"tree-pipeline"));
        store.Save("drive-account.dat",new DriveAccount { ClientId="test.apps.googleusercontent.com",ClientSecret="SYNTHETIC",RefreshToken="SYNTHETIC",Email="test@example.invalid",UserId="user-one" });
        var account=store.Load<DriveAccount>("drive-account.dat")!.Key;
        var source=Path.Combine(root,"pipeline-source"); Directory.CreateDirectory(source); var document=Path.Combine(source,"document.txt");
        await File.WriteAllTextAsync(document,"Version 1.");
        var plan=BackupPlan.CreateForTarget([source],BackupTargets.GoogleDrive,null,store.DirectoryPath); store.Save("backup-plan.dat",plan);
        await using var host=new HostServer(Path.Combine(root,"tree-pipeline-host"),0,System.Net.IPAddress.Loopback); host.Start();
        var client=new AgentClient(); var invitation=host.CreateInvitation("127.0.0.1"); var credentials=AgentClient.CreateCredentials(invitation,"Pipeline-PC"); await client.PairAsync(invitation,credentials);
        await using var backups=new BackupCoordinator(store,credentials.AgentId,()=>account);
        using var google=new FakeGoogle(); using var http=new HttpClient(google);
        var uploads=new CloudUploadCoordinator(store,http,credentials.AgentId,credentials.Name); uploads.SetHostAuthorized(true);
        async Task Drain()
        {
            for(var attempt=0;attempt<50;attempt++)
            {
                var local=backups.NextReport(); var cloud=uploads.NextReport();
                var response=await client.PollAsync(credentials,plan.Summary,local,cloudConfiguration:new("Connected","test@example.invalid","Authorized"),cloudReport:cloud);
                backups.Acknowledge(response.AcknowledgedJobId,response.AcknowledgedSequence); uploads.Acknowledge(response.AcknowledgedCloudJobId,response.AcknowledgedCloudSequence);
                if(local is null && cloud is null) return;
            }
            throw new Exception("Report outbox did not drain.");
        }
        async Task<Guid> Local()
        {
            await Drain(); var job=host.QueueBackup(credentials.AgentId).Command; backups.Handle(job);
            await Until(()=>!backups.IsBusy && backups.AllJobs().Any(j=>j.Command.JobId==job.JobId && j.Report.State=="Completed")); await Drain(); return job.JobId;
        }
        async Task Complete(Guid job)
        {
            uploads.Tick(backups.CompletedJobs()); await Until(()=>!uploads.IsBusy && uploads.AllUploads().Any(u=>u.JobId==job && u.Report.State is "Completed" or "Failed" or "Needs sign-in"));
            var report=uploads.AllUploads().Single(u=>u.JobId==job).Report;
            if(report.State!="Completed") throw new InvalidOperationException("Pipeline upload "+report.State+": "+report.Detail);
            await Drain();
        }
        try
        {
            var first=await Local(); await Complete(first);
            var firstFolder=host.GetJobs().Single().Cloud!.FileId;
            await File.WriteAllTextAsync(document,"Version 2."); var second=await Local(); await Complete(second);
            Check(host.GetJobs().Single(j=>j.Command.JobId==first).Cloud is { Superseded:true } &&
                host.GetSnapshots().Single().LastSuccessfulJob?.Command.JobId==second && host.GetJobs().Last().Cloud!.FileId==firstFolder,
                "host accepts a superseded annotation and offers only the current folder backup as the successful restore point");
            var removed=await new CloudCacheCleanup(store,http,credentials.AgentId).RunAsync(backups.AllJobs(),uploads.AllUploads(),backups.MarkTemporaryRemoved);
            Check(removed==2 && backups.AllJobs().All(j=>j.Report.TemporaryArchiveRemoved),"acknowledged newer folder backup permits cleanup of both owned local snapshots");
            Check(await new DriveRetention(store,http,credentials.AgentId).PruneAsync(uploads.AllUploads(),1,uploads.MarkTrashed)==0 && google.TrashedFiles==0,
                "version retention never trashes the live incremental folder or its files");
            await File.WriteAllTextAsync(document,"Version 3."); var third=await Local(); google.BadChecksum=true; uploads.Tick(backups.CompletedJobs());
            await Until(()=>!uploads.IsBusy && uploads.AllUploads().Any(u=>u.JobId==third && u.Report.State=="Failed")); await Drain();
            await File.WriteAllTextAsync(document,"Version 4."); var fourth=await Local(); var requests=google.DataRequests; uploads.Tick(backups.CompletedJobs()); await Task.Delay(100);
            Check(!uploads.IsBusy && google.DataRequests==requests && uploads.AllUploads().Single(u=>u.JobId==fourth).Report.State=="Queued",
                "a newer backup cannot overtake a failed older update and later roll the folder back");
            await uploads.DisposeAsync(); CloudUploadCoordinator.RetrySaved(store); google.BadChecksum=false;
            uploads=new(store,http,credentials.AgentId,credentials.Name); uploads.SetHostAuthorized(true); await Complete(third); await Complete(fourth);
            var downloaded=Path.Combine(root,"pipeline-latest.zip"); await new DriveDownloader(store,http).DownloadAsync(firstFolder!,downloaded);
            var result=await new RestoreEngine().RestoreAsync(downloaded,Path.Combine(root,"pipeline-latest"),new("GoogleDrive","latest"));
            Check(await File.ReadAllTextAsync(Path.Combine(result.PayloadDirectory,"pipeline-source","document.txt"))=="Version 4.","retry and restart finish in source snapshot order and restore the newest content");
        }
        finally { await uploads.DisposeAsync(); }
        Console.WriteLine("PASS rolling folder history, acknowledged cleanup and ordered retries over real TLS");
    }
    private static async Task Until(Func<bool> condition)
    { var deadline=DateTime.UtcNow.AddSeconds(25); while(!condition()) { if(DateTime.UtcNow>deadline) throw new TimeoutException("Incremental pipeline did not reach its expected state."); await Task.Delay(20); } }
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); Console.WriteLine("  PASS "+message); }
    private static async Task Reject(Func<Task> action) { try { await action(); } catch(Exception ex) when(ex is InvalidDataException or DriveException) { return; } throw new Exception("Expected rejected incremental operation."); }
}
