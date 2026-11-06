using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AutoBack.Backup;
using AutoBack.Connection;
using Microsoft.Win32.SafeHandles;

namespace AutoBack.Agent;

public sealed record FolderAccessIssue(string Path, string Reason);
public sealed record FolderAccessResult(string PlanId, string Account, DateTimeOffset CheckedUtc, int Files, int Directories,
    int ErrorCount, List<FolderAccessIssue> Issues, bool Complete, string? AuditFile = null)
{
    public bool Ready => Complete && ErrorCount == 0;
    public string Summary => Ready ? $"Ready: {Files} file{(Files == 1 ? "" : "s")} checked as {Account}; backup destination write/delete check passed."
        : $"Not ready: {ErrorCount} issue(s). {Files} files checked as {Account}.";
}

/// <summary>Checks actual file opens under the worker identity; grants never remove denies or take ownership.</summary>
public sealed class FolderAccess(bool serviceMode, ProtectedStore? store = null)
{
    private static readonly SecurityIdentifier ServiceSid = new(WellKnownSidType.LocalServiceSid, null);
    public FolderAccessResult Check(BackupPlan plan, CancellationToken token = default, Action<string>? progress = null)
        => Execute(plan, false, token, progress);
    public FolderAccessResult GrantAndCheck(BackupPlan plan, CancellationToken token = default, Action<string>? progress = null)
        => Execute(plan, true, token, progress);

    private FolderAccessResult Execute(BackupPlan plan, bool grant, CancellationToken token, Action<string>? progress)
    {
        var issues = new List<FolderAccessIssue>(); var errors = 0; var files = 0; var directories = 0;
        var account = serviceMode ? @"NT AUTHORITY\LocalService" : WindowsIdentity.GetCurrent().Name;
        void Issue(string path, string text) { errors++; if (issues.Count < 100) issues.Add(new(path, text)); }
        FolderAccessResult Result(bool complete, string? audit = null) => new(plan.PlanId, account, DateTimeOffset.UtcNow, files, directories, errors, issues, complete, audit);
        try { plan.Validate(); }
        catch (Exception ex) when (Expected(ex)) { Issue("Selected folders", Safe(ex.Message)); return Result(false); }
        SafeAccessTokenHandle? workerToken;
        try { workerToken = serviceMode ? ServiceIdentity.Acquire(token) : null; }
        catch (Exception ex) when (Expected(ex))
        {
            var nativeError = ex is Win32Exception native ? $" (Windows error {native.NativeErrorCode}: {new Win32Exception(native.NativeErrorCode).Message})" : "";
            Issue("Service account", "LocalService verification is unavailable: " + Safe(ex.Message + nativeError));
            return Result(false);
        }
        using var serviceToken = workerToken;
        using var current = WindowsIdentity.GetCurrent();
        if (grant && (!serviceMode || !new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator) || store is null))
        { Issue("Permissions", "Grant required access is available in administrator/service setup. Console mode checks your current Windows account."); return Result(false); }
        void AsWorker(Action action)
        {
            if (serviceToken is null) action();
            else WindowsIdentity.RunImpersonated(serviceToken, action);
        }
        if (serviceMode)
        {
            try { AsWorker(() => { using var identity = WindowsIdentity.GetCurrent(); if (identity.User?.IsWellKnown(WellKnownSidType.LocalServiceSid) != true) throw new InvalidOperationException("Worker identity did not match LocalService."); }); }
            catch (Exception ex) when (Expected(ex)) { Issue("Service account", "Cannot impersonate the Agent service account: " + Safe(ex.Message)); return Result(false); }
        }
        AccessAudit? audit = null;
        try
        {
            if (grant) audit = new(store!);
            // Validate and hold ancestors against rename/reparse substitution during this operation.
            using var sourceLeases = new LeaseSet();
            foreach (var source in plan.Sources) sourceLeases.AddChain(source);
            sourceLeases.AddChain(NearestExisting(plan.Destination));
            foreach (var parent in plan.Sources.SelectMany(Parents).Concat(Parents(plan.Destination)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(parent)) continue;
                try
                {
                    if (grant) Ensure(parent, true, FileSystemRights.ReadAttributes | FileSystemRights.Traverse, InheritanceFlags.None,
                        () => AsWorker(() => { _ = File.GetAttributes(parent); }), audit!);
                    else AsWorker(() => { _ = File.GetAttributes(parent); });
                }
                catch (Exception ex) when (Expected(ex)) { Issue(parent, Reason(ex)); }
            }
            foreach (var source in plan.Sources)
            {
                var pending = new Stack<string>(); pending.Push(source);
                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested(); var directory = pending.Pop();
                    progress?.Invoke($"Checking folders: {directories}; files: {files}. {directory}");
                    try
                    {
                        using var lease = NativeAccess.OpenMetadata(directory);
                        if (grant) GrantOne(directory, true, ServiceSid, FileSystemRights.ReadAndExecute,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            (before, after) => audit!.BeforeChange(directory, before, after));
                        string[] entries = [];
                        AsWorker(() => { _ = File.GetAttributes(directory); entries = Directory.GetFileSystemEntries(directory); }); directories++;
                        foreach (var entry in entries)
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                using var entryLease = NativeAccess.OpenMetadata(entry);
                                var attributes = File.GetAttributes(entry);
                                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                                void ReadFile() => AsWorker(() =>
                                {
                                    using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                                    _ = input.ReadByte(); // Proves data access, including EFS/locked-file errors; no full second copy.
                                });
                                if (grant) Ensure(entry, false, FileSystemRights.Read, InheritanceFlags.None, ReadFile, audit!);
                                ReadFile(); files++;
                            }
                            catch (Exception ex) when (Expected(ex)) { Issue(entry, Reason(ex)); }
                        }
                    }
                    catch (Exception ex) when (Expected(ex)) { Issue(directory, Reason(ex)); }
                }
            }
            token.ThrowIfCancellationRequested();
            try
            {
                if (grant)
                {
                    Directory.CreateDirectory(plan.Destination);
                    sourceLeases.AddChain(plan.Destination);
                    // Cache already has a private store ACL. Only Local only needs an explicit destination grant.
                    if (plan.Target != BackupTargets.GoogleDrive)
                        GrantOne(plan.Destination, true, ServiceSid, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            (before, after) => audit!.BeforeChange(plan.Destination, before, after));
                }
                AsWorker(() => { Directory.CreateDirectory(plan.Destination); ProbeDestination(plan.Destination); });
            }
            catch (Exception ex) when (Expected(ex)) { Issue(plan.Destination, "Backup destination: " + Reason(ex)); }
            progress?.Invoke($"Finished: {files} files, {directories} folders, {errors} issue(s).");
            return Result(true, audit?.FileName);
        }
        catch (Exception ex) when (Expected(ex)) { Issue("Access check", Reason(ex)); return Result(false, audit?.FileName); }
        finally { audit?.Dispose(); }
    }

    private static IEnumerable<string> Parents(string path)
    {
        var parents = new Stack<string>();
        for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent!)) parents.Push(parent!);
        return parents;
    }
    private static string NearestExisting(string path)
    { while (!Directory.Exists(path)) path = Path.GetDirectoryName(path) ?? throw new DirectoryNotFoundException("No existing destination parent."); return path; }
    private static void Ensure(string path, bool directory, FileSystemRights rights, InheritanceFlags inheritance, Action probe, AccessAudit audit)
    {
        try { probe(); return; }
        catch (UnauthorizedAccessException) { }
        // Sharing violations, links, EFS and other errors are reported rather than changing unrelated policy.
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Encrypted) != 0) throw new IOException("This Windows-encrypted (EFS) file needs a readable exported/decrypted copy for the service.");
        GrantOne(path, directory, ServiceSid, rights, inheritance, (before, after) => audit.BeforeChange(path, before, after));
        probe(); // Explicit denies and protected children must still fail visibly.
    }
    internal static void GrantOne(string path, bool directory, SecurityIdentifier sid, FileSystemRights rights, InheritanceFlags inheritance, Action<string, string> beforeChange)
    {
        using var lease = NativeAccess.OpenMetadata(path, !directory);
        FileSystemSecurity acl = directory ? new DirectorySecurity(path, AccessControlSections.Access) : new FileSecurity(path, AccessControlSections.Access);
        var before = acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var rule = new FileSystemAccessRule(sid, rights, inheritance, PropagationFlags.None, AccessControlType.Allow);
        if (acl is DirectorySecurity folder) folder.AddAccessRule(rule); else ((FileSecurity)acl).AddAccessRule(rule);
        var after = acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        if (before == after) return;
        beforeChange(before, after); // Persist before changing a DACL. Ownership, denies and audit policy are untouched.
        NativeAccess.SetThisObjectDacl(path, acl.GetSecurityDescriptorBinaryForm());
    }
    private static void ProbeDestination(string path)
    {
        BackupPlan.CheckNoReparse(path);
        var directory = Path.Combine(path, ".autoback-access-" + Guid.NewGuid().ToString("N"));
        var probe = Path.Combine(directory, "probe.tmp");
        var created = false; var folderCreated = false;
        try
        {
            if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Access-check scratch path already exists.");
            Directory.CreateDirectory(directory); folderCreated = true; BackupPlan.CheckNoReparse(directory);
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose))
            { created = true; stream.WriteByte(0x41); stream.Flush(true); stream.Position = 0; if (stream.ReadByte() != 0x41) throw new IOException("Destination readback failed."); }
            created = false; Directory.Delete(directory, false); folderCreated = false;
        }
        finally
        {
            if (created && File.Exists(probe)) File.Delete(probe);
            if (folderCreated) { BackupPlan.CheckNoReparse(directory); Directory.Delete(directory, false); }
        } // Only this operation's exact newly-created file and empty scratch directory; never recursive.
    }
    private static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or InvalidOperationException or System.Security.SecurityException;
    private static string Safe(string value) => new(value.Select(c => char.IsControl(c) ? ' ' : c).Take(1000).ToArray());
    private static string Reason(Exception ex) => ex is UnauthorizedAccessException || ex is Win32Exception denied && denied.NativeErrorCode == 5 ? "Access denied. Grant required access, then check again. Explicit deny rules/EFS may need the folder owner."
        : (ex is IOException && ((ex.HResult & 0xffff) is 32 or 33)) || (ex is Win32Exception native && native.NativeErrorCode is 32 or 33) ? "File is locked/in use. Close the program using it, then check again."
        : Safe(ex.Message);

    private sealed class AccessAudit : IDisposable
    {
        private readonly FileStream _file;
        private readonly bool _machine;
        public string FileName { get; }
        internal AccessAudit(ProtectedStore store)
        {
            _machine = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            FileName = "permission-audit-" + Guid.NewGuid().ToString("N") + ".dat";
            _file = new(Path.Combine(store.DirectoryPath, FileName), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        internal void BeforeChange(string path, string before, string after)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, path, before, after, timeUtc = DateTimeOffset.UtcNow }, Protocol.Json);
            try
            {
                var cipher = ProtectedData.Protect(plain, null, _machine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser);
                var line = Encoding.ASCII.GetBytes(Convert.ToBase64String(cipher) + "\n"); _file.Write(line, 0, line.Length); _file.Flush(true);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        public void Dispose() => _file.Dispose();
    }
    private sealed class LeaseSet : IDisposable
    {
        private readonly Dictionary<string, SafeFileHandle> _handles = new(StringComparer.OrdinalIgnoreCase);
        internal void AddChain(string path)
        {
            foreach (var entry in Parents(path).Concat(new[] { path })) if (!_handles.ContainsKey(entry)) _handles.Add(entry, NativeAccess.OpenMetadata(entry));
        }
        public void Dispose() { foreach (var handle in _handles.Values) handle.Dispose(); }
    }
    private static class NativeAccess
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfoNative information);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetFileSecurity(string path, uint information, byte[] descriptor);
        [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative
        { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
        internal static SafeFileHandle OpenMetadata(string path, bool requireSingleLink = false)
        {
            // No DELETE sharing: keep this exact object in place while its path is inspected/updated.
            var handle = CreateFile(path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Cannot inspect " + path); }
            if (!GetFileInformationByHandle(handle, out var info)) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Cannot inspect " + path); }
            if ((info.Attributes & 0x400) != 0) { handle.Dispose(); throw new IOException("Links/junctions/cloud placeholders are not supported: " + path); }
            if (requireSingleLink && info.Links > 1) { handle.Dispose(); throw new IOException("Permission changes on hard-linked files are refused. Choose a readable independent copy: " + path); }
            return handle;
        }
        // SetFileSecurity changes exactly this DACL without walking unrelated siblings/descendants.
        // Descendants of selected sources are visited explicitly, so protected ACLs and links are handled individually.
        internal static void SetThisObjectDacl(string path, byte[] descriptor)
        { if (!SetFileSecurity(path, 4, descriptor)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not grant required access to " + path); }
    }
}
