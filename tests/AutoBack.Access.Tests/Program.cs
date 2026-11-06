using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Forms;
using AutoBack.Agent;
using AutoBack.Backup;
using AutoBack.Connection;

internal static class Program
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateHardLink(string name,string existing,IntPtr reserved);
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length>0 && args[0]=="--service-pilot")return ServicePilot.Run(args.Skip(1).ToArray());
        var parent=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"AutoBack-access-tests"));
        var root=Path.Combine(parent,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            Application.EnableVisualStyles();
            using(var setup=new PairingSetupForm("Test","localhost","Saved pairing"){StartPosition=FormStartPosition.Manual,Location=new(-16000,-16000),ShowInTaskbar=false})
            {setup.Show();Application.DoEvents();setup.GoogleButton.PerformClick();Check(setup.SelectedAction=="google-sign-in"&&setup.DialogResult==DialogResult.OK,"Google sign-in can be selected directly without folder setup");}
            var source=Path.Combine(root,"Documents");Directory.CreateDirectory(source);Directory.CreateDirectory(Path.Combine(source,"empty"));
            var file=Path.Combine(source,"document.txt");File.WriteAllText(file,"Synthetic access check source.");
            var destination=Path.Combine(root,"backups");var store=new ProtectedStore(Path.Combine(root,"store"));
            var plan=BackupPlan.CreateForTarget([source],BackupTargets.LocalOnly,destination,store.DirectoryPath);
            var access=new FolderAccess(false,store);
            using(var cancelIdentity=new CancellationTokenSource())
            {
                cancelIdentity.Cancel();
                try { using var identity=ServiceIdentity.Acquire(cancelIdentity.Token);throw new Exception("Expected identity cancellation."); }
                catch(OperationCanceledException){Console.WriteLine("PASS cancelled service verification does not start a helper");}
            }
            try { ServiceIdentity.RunWorker(["access-identity-worker","not-a-service","not-a-nonce"]);throw new Exception("Expected helper argument rejection."); }
            catch(ArgumentException){Console.WriteLine("PASS helper rejects malformed internal command arguments");}
            try { ServiceIdentity.RunWorker(["access-identity-worker","AutoBackAccess-"+Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N")]);throw new Exception("Expected non-service identity rejection."); }
            catch(UnauthorizedAccessException){Console.WriteLine("PASS interactive callers cannot run the internal LocalService helper");}
            var ready=access.Check(plan);
            Check(ready.Ready&&ready.Files==1&&ready.Directories==2,"real file enumeration/open and destination create/write/read/delete check pass");
            Check(!Directory.EnumerateFileSystemEntries(destination).Any(),"destination scratch directory/file are removed without touching unrelated data");
            using(var locked=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {var failed=access.Check(plan);Check(!failed.Ready&&failed.ErrorCount>0&&failed.Issues.Any(i=>i.Reason.Contains("locked",StringComparison.OrdinalIgnoreCase)),"locked source file is reported and never considered ready");}
            using(var cancel=new CancellationTokenSource()){cancel.Cancel();try{access.Check(plan,cancel.Token);throw new Exception("Expected cancellation.");}catch(OperationCanceledException){Console.WriteLine("PASS cancelled check produces no ready result");}}
            var sid=new SecurityIdentifier(WellKnownSidType.LocalServiceSid,null);
            var parentFolder=Path.Combine(root,"metadata-parent");Directory.CreateDirectory(parentFolder);
            var sibling=Path.Combine(parentFolder,"unselected.txt");File.WriteAllText(sibling,"KEEP");
            var beforeSibling=new FileInfo(sibling).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            var before=new DirectoryInfo(parentFolder).GetAccessControl();var originalOwner=before.GetOwner(typeof(SecurityIdentifier));
            var auditCalled=false;
            FolderAccess.GrantOne(parentFolder,true,sid,FileSystemRights.ReadAttributes|FileSystemRights.Traverse,InheritanceFlags.None,(oldValue,newValue)=>{auditCalled=true;Check(oldValue!=newValue,"permission change is recorded before being applied");});
            var after=new DirectoryInfo(parentFolder).GetAccessControl();
            var rule=after.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Single(r=>r.IdentityReference.Equals(sid));
            Check(auditCalled&&rule.InheritanceFlags==InheritanceFlags.None&&(rule.FileSystemRights&FileSystemRights.ListDirectory)==0&&(rule.FileSystemRights&FileSystemRights.WriteData)==0,"parent grant is metadata/traverse only, with no list/write/inheritance");
            Check(Equals(after.GetOwner(typeof(SecurityIdentifier)),originalOwner)&&after.AreAccessRulesProtected==before.AreAccessRulesProtected&&new FileInfo(sibling).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access)==beforeSibling,"parent owner/protection and unselected sibling ACL stay unchanged");
            var protectedFolder=Path.Combine(root,"protected-source");Directory.CreateDirectory(protectedFolder);
            var protectedAcl=new DirectoryInfo(protectedFolder).GetAccessControl();protectedAcl.SetAccessRuleProtection(true,true);new DirectoryInfo(protectedFolder).SetAccessControl(protectedAcl);
            FolderAccess.GrantOne(protectedFolder,true,sid,FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,(_,_)=>{});
            Check(new DirectoryInfo(protectedFolder).GetAccessControl().AreAccessRulesProtected,"grant preserves a protected folder's inheritance setting");
            try{FolderAccess.GrantOne(file,false,sid,FileSystemRights.Read,InheritanceFlags.None,(_,_)=>throw new IOException("Audit write failed"));throw new Exception("Expected audit failure.");}
            catch(IOException){Check(!new FileInfo(file).GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r=>r.IdentityReference.Equals(sid)),"failed audit persistence prevents the ACL mutation");}
            var hardLink=Path.Combine(root,"hard-link.txt");
            if(!CreateHardLink(hardLink,file,IntPtr.Zero))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try{FolderAccess.GrantOne(hardLink,false,sid,FileSystemRights.Read,InheritanceFlags.None,(_,_)=>{});throw new Exception("Expected hard-link refusal.");}
            catch(IOException){Check(File.ReadAllText(file)=="Synthetic access check source.","hard-linked file ACL changes are refused to protect other paths");}
            var foreign=Path.Combine(root,"unselected");Directory.CreateDirectory(foreign);File.WriteAllText(Path.Combine(foreign,"keep.txt"),"UNSELECTED");
            var junction=Path.Combine(source,"junction");var start=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"/c","mklink","/J",junction,foreign})start.ArgumentList.Add(arg);
            using(var process=Process.Start(start)!){process.WaitForExit();Check(process.ExitCode==0,"test junction created inside the isolated fixture");}
            var linkCheck=access.Check(plan);Check(!linkCheck.Ready&&linkCheck.Issues.Any(i=>i.Path==junction)&&File.ReadAllText(Path.Combine(foreign,"keep.txt"))=="UNSELECTED","junction is reported without following or changing its target");Directory.Delete(junction,false);
            using(var form=new BackupFoldersForm(dataDirectory:store.DirectoryPath){StartPosition=FormStartPosition.Manual,Location=new(-16000,-16000),ShowInTaskbar=false})
            {
                form.Show();Application.DoEvents();form.AddFolder(source);form.LocalOnlyMode.Checked=true;form.DestinationInput.Text=destination;
                Pump(form.RunAccessAsync(false));Check(form.AccessResult?.Ready==true&&form.AccessDetails.Text.Contains("Ready"),"folder UI displays actual access result: "+form.AccessDetails.Text);
                form.DestinationInput.Text=Path.Combine(root,"different-backups");Check(form.AccessResult is null,"changing a destination invalidates the previous ready result");form.Close();
            }
            using(var form=new BackupFoldersForm(dataDirectory:store.DirectoryPath,serviceMode:true,store:store){StartPosition=FormStartPosition.Manual,Location=new(-16000,-16000),ShowInTaskbar=false})
            {
                // A denied source makes the service check fail even when an elevated test caller can obtain its token.
                var deny=new DirectoryInfo(source).GetAccessControl();deny.AddAccessRule(new(sid,FileSystemRights.ListDirectory,AccessControlType.Deny));new DirectoryInfo(source).SetAccessControl(deny);
                form.Show();Application.DoEvents();form.AddFolder(source);form.SaveButton.PerformClick();
                var deadline=DateTime.UtcNow.AddSeconds(15);while((form.AccessResult is null||!form.CheckAccessButton.Enabled)&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(20);}
                Check(form.AccessResult?.Ready==false&&!form.SaveButton.Enabled&&form.SelectedPlan is null&&form.DialogResult!=DialogResult.OK,"service-mode Save cannot bypass a failed/unavailable worker-identity check");
                var repo=new DirectoryInfo(AppContext.BaseDirectory);while(repo is not null&&!File.Exists(Path.Combine(repo.FullName,"AutoBack.slnx")))repo=repo.Parent;
                using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new(0,0,form.Width,form.Height));bitmap.Save(Path.Combine(repo!.FullName,"docs","folder-access-preview.png"));}
                var evidence=Path.Combine(repo!.FullName,"artifacts","access-validation");Directory.CreateDirectory(evidence);
                store.Save("backup-plan.dat",plan);
                var executable=Path.Combine(repo.FullName,"src","AutoBack.Agent","bin","Release","net10.0-windows","AutoBack.Agent.exe");
                int Cli(string command)
                {
                    var commandStart=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                    foreach(var arg in new[]{command,"--data-dir",store.DirectoryPath})commandStart.ArgumentList.Add(arg);
                    using var process=Process.Start(commandStart)!;
                    if(!process.WaitForExit(20000)){process.Kill(entireProcessTree:true);throw new TimeoutException("Access CLI timed out.");}
                    _=process.StandardOutput.ReadToEnd();_=process.StandardError.ReadToEnd();return process.ExitCode;
                }
                Check(Cli("access-check")==0&&store.Load<FolderAccessResult>("folder-access.dat")?.Ready==true,"actual CLI checks saved folders and persists its protected result");
                var savedPlanBytes=File.ReadAllBytes(Path.Combine(store.DirectoryPath,"backup-plan.dat"));
                Check(Cli("access-grant")!=0&&savedPlanBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(store.DirectoryPath,"backup-plan.dat"))),"console scope refuses service grants and preserves the saved folder plan");
                File.WriteAllText(Path.Combine(evidence,"result.json"),JsonSerializer.Serialize(new{verifiedAtUtc=DateTimeOffset.UtcNow,fileOpenChecks=true,lockedFilesDetected=true,minimalParentGrant=true,siblingAclPreserved=true,auditFailureSafe=true,linksRefused=true,saveGated=true,administratorTestProcess=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),elevatedServiceGrantAndCheckTested=false,physicalWindows7Tested=false},Protocol.Json));
                form.Close();
            }
            Console.WriteLine("ALL FOLDER ACCESS CHECKS PASSED. Elevated LocalService full grant/check still needs installed-setup pilot.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{if(Directory.Exists(root)&&Path.GetDirectoryName(Path.GetFullPath(root))==parent&&Guid.TryParseExact(Path.GetFileName(root),"N",out _))Directory.Delete(root,true);}
    }
    private static void Pump(Task task){while(!task.IsCompleted){Application.DoEvents();Thread.Sleep(10);}task.GetAwaiter().GetResult();Application.DoEvents();}
    private static void Check(bool value,string text){if(!value)throw new InvalidOperationException(text);Console.WriteLine("PASS "+text);}
}
