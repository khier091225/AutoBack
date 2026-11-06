using System.Security.Cryptography;
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
        var parent = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AutoBack-legacy-unit"));
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Check(IntPtr.Size == 4, "actual x86 .NET Framework process");
            Check(LegacyPath.IsPathFullyQualified(@"C:\Files") && !LegacyPath.IsPathFullyQualified(@"C:Files") && !LegacyPath.IsPathFullyQualified(@"\Files"), "fully-qualified paths only");
            var ciphertext = new byte[16]; var tag = new byte[16];
            using (var aes = new AesGcm(new byte[32], 16))
            {
                aes.Encrypt(new byte[12], new byte[16], ciphertext, tag, []);
                Check(Convert.ToHexString(ciphertext) == "CEA7403D4D606B6E074EC5D3BAF39D18" && Convert.ToHexString(tag) == "D0D1C8A799996BF0265B98B5D48AB919", "AES-256-GCM published known-answer vector");
                var plain = new byte[16]; aes.Decrypt(new byte[12], ciphertext, tag, plain, []);
                tag[0] ^= 1; var rejected = false;
                try { aes.Decrypt(new byte[12], ciphertext, tag, plain, []); } catch (AuthenticationTagMismatchException) { rejected = true; }
                Check(rejected && plain.All(b => b == 0), "tampered authentication tag rejected without plaintext");
            }
            var hkdf = HKDF.DeriveKey(HashAlgorithmName.SHA256, Enumerable.Repeat((byte)0x0b, 22).ToArray(), 42,
                Convert.FromHexString("000102030405060708090A0B0C"), Convert.FromHexString("F0F1F2F3F4F5F6F7F8F9"));
            Check(Convert.ToHexString(hkdf) == "3CB25F25FAACD57A90434F64D0362F2A2D2D0A90CF1A5A4C5DB02D56ECC4C5BF34007208D5B887185865", "HKDF-SHA256 RFC 5869 test vector");
            var source1 = Path.Combine(root, "Documents"); var source2 = Path.Combine(root, "Work"); Directory.CreateDirectory(source1); Directory.CreateDirectory(source2);
            System.IO.File.WriteAllBytes(Path.Combine(source1, "same-name.bin"), RandomNumberGenerator.GetBytes(2 * ArchiveFile.ChunkSize + 111));
            File.WriteAllText(Path.Combine(source2, "same-name.txt"), "Synthetic legacy test data.");
            using (var form = new BackupFoldersForm(dataDirectory: Path.Combine(root, "ui")))
            { form.AddFolder(source1); Check(form.GoogleDriveMode.Checked && form.TryBuildPlan(out var picked) && picked!.Target == BackupTargets.GoogleDrive, "Framework folder picker defaults to Google Drive"); }
            SynchronizationContext.SetSynchronizationContext(null);
            RunAsync(root, [source1, source2]).GetAwaiter().GetResult();
            Console.WriteLine("ALL LEGACY FRAMEWORK CHECKS PASSED (Google HTTP simulated; Windows 7 not tested)."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root) && System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(root)) == parent && Guid.TryParseExact(System.IO.Path.GetFileName(root), "N", out _)) Directory.Delete(root, true); }
    }
    private static async Task RunAsync(string root, string[] sources)
    {
        var store = new ProtectedStore(Path.Combine(root, "agent"));
        store.Save("drive-account.dat", new DriveAccount { ClientId = "client.apps.googleusercontent.com", ClientSecret = "SYNTHETIC", RefreshToken = "SYNTHETIC", Email = "test@example.invalid", UserId = "user-one" });
        var recovery = Path.Combine(root, "recovery.abkey"); const string password = "Synthetic test passphrase 2026";
        EncryptionVault.Enable(store, recovery, password);
        using var google = new FakeGoogle { LoseFinalResponse = true }; using var http = new HttpClient(google);
        var account = store.Load<DriveAccount>("drive-account.dat")!.Key; var agent = Guid.NewGuid();
        var plan = BackupPlan.CreateForTarget(sources, BackupTargets.GoogleDrive, null, store.DirectoryPath); store.Save("backup-plan.dat", plan);
        await using var backups = new BackupCoordinator(store, agent, () => account);
        backups.Handle(new(Guid.NewGuid(), plan.PlanId, DateTimeOffset.UtcNow, Encrypt: true));
        await Until(() => backups.AllJobs().Any(j => BackupStates.Terminal(j.Report.State)) && !backups.IsBusy);
        var local = backups.AllJobs().Single(); Check(local.Report.State == "Completed" && local.Report.Encrypted, "multi-folder chunked encrypted backup prepared and verified");
        backups.Acknowledge(local.Report.JobId, local.Report.Sequence);
        await using (var uploads = new CloudUploadCoordinator(store, http, agent, "Legacy-x86"))
        {
            uploads.SetHostAuthorized(true); uploads.Tick(backups.CompletedJobs());
            await Until(() => !uploads.IsBusy && uploads.AllUploads().Single().Report.State == "Retrying");
            Check(File.Exists(local.Report.ArchivePath), "failed upload retains the temporary archive");
        }
        CloudUploadCoordinator.RetrySaved(store);
        await using var resumed = new CloudUploadCoordinator(store, http, agent, "Legacy-x86"); resumed.SetHostAuthorized(true); resumed.Tick(backups.CompletedJobs());
        await Until(() => !resumed.IsBusy && resumed.AllUploads().Single().Report.State == "Completed");
        var cloud = resumed.AllUploads().Single(); resumed.Acknowledge(cloud.Report.JobId, cloud.Report.Sequence);
        Check(google.BinaryFiles == 1, "resumable upload reuses the same cloud object");
        var cleanup = new CloudCacheCleanup(store, http, agent);
        Check(await cleanup.RunAsync(backups.AllJobs(), resumed.AllUploads(), backups.MarkTemporaryRemoved) == 1 && !File.Exists(local.Report.ArchivePath), "verified cloud backup cleans only its temporary archive");
        var downloaded = Path.Combine(root, "download.abk");
        await new DriveDownloader(store, http).DownloadAsync(cloud.Report.FileId!, downloaded);
        var result = await new RestoreEngine().RestoreAsync(downloaded, Path.Combine(root, "restore"), new("GoogleDrive", cloud.Report.FileId!), keys: EncryptionVault.Load(store).Resolve);
        Check(result.Files == 2 && result.Bytes > 2 * ArchiveFile.ChunkSize, "cloud restore works after cache cleanup");
        var localPlan = BackupPlan.CreateForTarget(sources, BackupTargets.LocalOnly, Path.Combine(root, "permanent"), store.DirectoryPath); store.Save("backup-plan.dat", localPlan);
        backups.Handle(new(Guid.NewGuid(), localPlan.PlanId, DateTimeOffset.UtcNow, Encrypt: true));
        await Until(() => !backups.IsBusy && backups.AllJobs().Count == 2);
        resumed.Tick(backups.CompletedJobs());
        Check(backups.AllJobs().Last().Report.State == "Completed" && File.Exists(backups.AllJobs().Last().Report.ArchivePath) && resumed.AllUploads().Count == 1, "Local only retains the archive and creates no upload despite connected Google");
        var imported = new ProtectedStore(Path.Combine(root, "import")); EncryptionVault.Import(imported, recovery, password);
        Check(EncryptionVault.Load(imported).Keys.Count == 1, "password-protected recovery key export/import");
        Console.WriteLine("PASS Framework upload, retry, cleanup, restore and Local only.");
    }
    private static void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); Console.WriteLine("PASS " + description); }
    private static async Task Until(Func<bool> condition) { var deadline = DateTime.UtcNow.AddSeconds(45); while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Legacy test condition timed out."); await Task.Delay(50); } }
}
