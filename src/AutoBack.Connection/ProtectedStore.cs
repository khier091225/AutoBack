using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace AutoBack.Connection;

/// <summary>Atomic DPAPI storage. Machine-scoped agent data also has an explicit directory ACL.</summary>
public sealed class ProtectedStore
{
    private readonly string _directory;
    private readonly DataProtectionScope _scope;

    public ProtectedStore(string directory, bool machineScope = false)
    {
        if (machineScope)
        {
            using var current = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator) &&
                !current.User!.IsWellKnown(WellKnownSidType.LocalSystemSid) && !current.User.IsWellKnown(WellKnownSidType.LocalServiceSid))
                throw new UnauthorizedAccessException("Machine-wide credentials require an elevated administrator or the agent service identity.");
        }
        _directory = Path.GetFullPath(directory);
        _scope = machineScope ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;
        Directory.CreateDirectory(_directory);
        var marker = Path.Combine(_directory, ".autoback-store");
        var expectedMarker = $"AutoBack protected store v1/{_scope}";
        if (Directory.EnumerateFileSystemEntries(_directory).Any() &&
            (!File.Exists(marker) || File.ReadAllText(marker) != expectedMarker))
            throw new InvalidOperationException("Use a dedicated, empty AutoBack data directory. Existing unrelated directories are not modified.");
        // Restrict access before writing a machine-wide credential. DPAPI machine scope alone is insufficient.
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var identities = machineScope
            ? new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null) }
            : new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
        foreach (var identity in identities)
            acl.AddAccessRule(new(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_directory).SetAccessControl(acl);
        File.WriteAllText(marker, expectedMarker);
    }

    private string FilePath(string name)
    {
        if (Path.GetFileName(name) != name || name is "." or "..") throw new ArgumentException("Use a simple file name.", nameof(name));
        return Path.Combine(_directory, name);
    }

    public T? Load<T>(string name)
    {
        var path = FilePath(name);
        if (!File.Exists(path)) return default;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var encrypted = new MemoryStream();
        input.CopyTo(encrypted);
        var bytes = ProtectedData.Unprotect(encrypted.ToArray(), null, _scope);
        try { return JsonSerializer.Deserialize<T>(bytes, Protocol.Json) ?? throw new InvalidDataException("Empty protected file."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Delete(string name) => File.Delete(FilePath(name));

    public void Save<T>(string name, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, null, _scope);
            var path = FilePath(name);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    file.Write(encrypted);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
