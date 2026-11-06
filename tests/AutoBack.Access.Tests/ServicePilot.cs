using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using AutoBack.Agent;
using AutoBack.Backup;
using AutoBack.Connection;

internal static class ServicePilot
{
    internal static int Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Provide the agent executable and report path.");
        var report = Path.GetFullPath(args[1]);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoBack-ServiceIdentityPilot-" + Guid.NewGuid().ToString("N"));
        var checks = new List<string>();
        int exit;
        string? error = null;
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Run this isolated LocalService pilot as administrator.");
            Directory.CreateDirectory(root);
            var rootAcl = new DirectorySecurity();
            rootAcl.SetAccessRuleProtection(true, false);
            foreach(var sid in new[]{WellKnownSidType.BuiltinAdministratorsSid,WellKnownSidType.LocalSystemSid})
                rootAcl.AddAccessRule(new(new SecurityIdentifier(sid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            new DirectoryInfo(root).SetAccessControl(rootAcl);
            // Stage only application/runtime files in an isolated, service-readable location.
            // A development checkout under a private user profile is not executable by LocalService.
            var workerDirectory=Path.Combine(root,"worker");Directory.CreateDirectory(workerDirectory);
            var workerAcl=new DirectorySecurity();workerAcl.SetAccessRuleProtection(true,false);
            foreach(var sid in new[]{WellKnownSidType.BuiltinAdministratorsSid,WellKnownSidType.LocalSystemSid})
                workerAcl.AddAccessRule(new(new SecurityIdentifier(sid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            workerAcl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalServiceSid,null),FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            new DirectoryInfo(workerDirectory).SetAccessControl(workerAcl);
            var originalExecutable=Path.GetFullPath(args[0]);var applicationDirectory=Path.GetDirectoryName(originalExecutable)!;
            foreach(var runtimeFile in Directory.EnumerateFiles(applicationDirectory,"*",SearchOption.AllDirectories))
            {
                var name=Path.GetFileName(runtimeFile);
                if(Path.GetExtension(runtimeFile)!=".dll"&&name!=Path.GetFileName(originalExecutable)&&name!=Path.GetFileName(originalExecutable)+".config"&&!name.EndsWith(".deps.json",StringComparison.Ordinal)&&!name.EndsWith(".runtimeconfig.json",StringComparison.Ordinal))continue;
                if((File.GetAttributes(runtimeFile)&FileAttributes.ReparsePoint)!=0)throw new IOException("Runtime staging refuses links.");
                var staged=Path.Combine(workerDirectory,Path.GetRelativePath(applicationDirectory,runtimeFile));Directory.CreateDirectory(Path.GetDirectoryName(staged)!);File.Copy(runtimeFile,staged);
            }
            var executable=Path.Combine(workerDirectory,Path.GetFileName(originalExecutable));
            var source = Path.Combine(root,"source");Directory.CreateDirectory(source);
            var file = Path.Combine(source,"sample.txt");File.WriteAllText(file,"Isolated LocalService identity regression fixture.");
            var store = new ProtectedStore(Path.Combine(root,"store"),true);
            var destination = Path.Combine(root,"backups");
            var plan = BackupPlan.CreateForTarget([source],BackupTargets.LocalOnly,destination,store.DirectoryPath);
            store.Save("backup-plan.dat",plan);
            int Cli(string command)
            {
                var start = new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                foreach(var arg in new[]{command,"--machine","--data-dir",store.DirectoryPath})start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
                if(!process.WaitForExit(45000)){process.Kill(entireProcessTree:true);throw new System.TimeoutException("Service identity pilot command timed out.");}
                Console.WriteLine(stdout.GetAwaiter().GetResult());Console.WriteLine(stderr.GetAwaiter().GetResult());
                return process.ExitCode;
            }
            void Check(bool value,string description)
            { if(!value)throw new InvalidOperationException(description);checks.Add(description);Console.WriteLine("PASS "+description); }
            Check(Cli("access-check")!=0&&store.Load<FolderAccessResult>("folder-access.dat") is {Ready:false} before&&before.Issues.All(i=>i.Path!="Service account"),"real LocalService identity obtained before any grants; inaccessible folders fail");
            Check(Cli("access-grant")==0&&store.Load<FolderAccessResult>("folder-access.dat") is {Ready:true,Files:1,AuditFile:not null},"administrator grant and actual LocalService file/destination probes succeed");
            Check(Cli("access-check")==0&&store.Load<FolderAccessResult>("folder-access.dat") is {Ready:true,Files:1},"fresh independent service-account verification succeeds");
            using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                Check(Cli("access-check")!=0&&store.Load<FolderAccessResult>("folder-access.dat") is {Ready:false} denied&&denied.Issues.Any(i=>i.Reason.Contains("locked",StringComparison.OrdinalIgnoreCase)),"locked files still stop readiness after helper fallback");
            Check(!Directory.EnumerateFileSystemEntries(destination).Any(),"destination probes leave no scratch files");
            var remaining=ServiceController.GetServices();
            try{Check(!remaining.Any(s=>s.ServiceName.StartsWith("AutoBackAccess-",StringComparison.Ordinal)),"temporary identity services are removed");}
            finally{foreach(var service in remaining)service.Dispose();}
            exit=0;
        }
        catch(Exception exception){error=exception.ToString();Console.Error.WriteLine(error);exit=1;}
        finally
        {
            // Delete only this pilot's exact newly-created fixture below ProgramData.
            if(Directory.Exists(root)&&Path.GetDirectoryName(Path.GetFullPath(root))==Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)&&Path.GetFileName(root).StartsWith("AutoBack-ServiceIdentityPilot-",StringComparison.Ordinal)&&Guid.TryParseExact(Path.GetFileName(root).Substring("AutoBack-ServiceIdentityPilot-".Length),"N",out _))
                Directory.Delete(root,true);
        }
        File.WriteAllText(report,JsonSerializer.Serialize(new{success=exit==0,verifiedUtc=DateTimeOffset.UtcNow,checks,error},Protocol.Json));
        return exit;
    }
}
