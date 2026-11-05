using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBack.Connection;

namespace AutoBack.Backup;

// Deliberately not a record: generated ToString must never include key material.
public sealed class ArchiveKey
{
    public required Guid Id { get; init; }
    public required string Material { get; init; }
    public byte[] Bytes()
    {
        if (Id == Guid.Empty || !Protocol.ValidSecret(Material)) throw new InvalidDataException("Invalid backup encryption key.");
        return Convert.FromHexString(Material);
    }
}
public sealed class EncryptionVault
{
    public List<ArchiveKey> Keys { get; init; } = [];
    public Guid? ActiveKeyId { get; init; }
    public bool Enabled { get; init; }
    public ArchiveKey Resolve(Guid id) => Keys.SingleOrDefault(k => k.Id == id) ?? throw new CryptographicException("The archive's recovery key is missing. Import its password-protected recovery file on this client.");
    public ArchiveKey? Active => Enabled ? Resolve(ActiveKeyId ?? throw new InvalidDataException("No active encryption key configured.")) : null;
    public static EncryptionVault Load(ProtectedStore store)
    {
        var vault = store.Load<EncryptionVault>("encryption.dat") ?? new();
        if (vault.Keys is null || vault.Keys.Count > 100 || vault.Keys.Any(k => k is null) || vault.Keys.Select(k => k.Id).Distinct().Count() != vault.Keys.Count)
            throw new InvalidDataException("Invalid encryption key store.");
        foreach (var key in vault.Keys) { var bytes = key.Bytes(); CryptographicOperations.ZeroMemory(bytes); }
        _ = vault.Active;
        return vault;
    }

    public static void Enable(ProtectedStore store, string recoveryFile, string password)
    {
        var old = Load(store);
        var key = old.ActiveKeyId is { } id ? old.Resolve(id) : new ArchiveKey { Id = Guid.NewGuid(), Material = Protocol.NewSecret() };
        var keys = old.Keys.Any(k => k.Id == key.Id) ? old.Keys : [.. old.Keys, key];
        var vault = new EncryptionVault { Keys = keys, ActiveKeyId = key.Id, Enabled = true };
        Export(vault, recoveryFile, password);
        store.Save("encryption.dat", vault); // Enable only after a successful, independently decrypted export.
    }

    public static void Disable(ProtectedStore store)
    {
        var old = Load(store);
        store.Save("encryption.dat", new EncryptionVault { Keys = old.Keys, ActiveKeyId = old.ActiveKeyId, Enabled = false });
    }

    public static void Export(EncryptionVault vault, string file, string password)
    {
        if (password.Length is < 12 or > 1024) throw new ArgumentException("Use a recovery passphrase of 12–1024 characters.");
        if (vault.Keys.Count == 0) throw new InvalidOperationException("No encryption key exists to export.");
        file = Path.GetFullPath(file);
        BackupPlan.CheckNoReparse(Path.GetDirectoryName(file)!);
        if (File.Exists(file) || Directory.Exists(file)) throw new IOException("Choose a new recovery file; exports never overwrite files.");
        var header = new byte[56];
        "ABKEY001"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 600_000);
        RandomNumberGenerator.Fill(header.AsSpan(12)); // 32-byte salt, 12-byte nonce.
        var key = Rfc2898DeriveBytes.Pbkdf2(password, header.AsSpan(12, 32), 600_000, HashAlgorithmName.SHA256, 32);
        var json = JsonSerializer.SerializeToUtf8Bytes(new RecoveryBundle(1, vault.Keys), Protocol.Json);
        var encrypted = new byte[json.Length + 16];
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".partial";
        var created = false;
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(header.AsSpan(44, 12), json, encrypted.AsSpan(0, json.Length), encrypted.AsSpan(json.Length), header);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { created = true; output.Write(header); output.Write(encrypted); output.Flush(true); }
            var check = ReadRecovery(temporary, password);
            if (check.Count != vault.Keys.Count || vault.Keys.Any(k => check.All(c => c.Id != k.Id || c.Material != k.Material)))
                throw new CryptographicException("The recovery export could not be verified.");
            File.Move(temporary, file);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(json);
            if (created && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Import(ProtectedStore store, string file, string password)
    {
        var imported = ReadRecovery(file, password);
        var old = Load(store);
        var keys = old.Keys.ToList();
        foreach (var key in imported)
        {
            var existing = keys.SingleOrDefault(k => k.Id == key.Id);
            if (existing is not null && existing.Material != key.Material) throw new CryptographicException("A recovery key conflicts with an existing key ID.");
            if (existing is null) keys.Add(key);
        }
        if (keys.Count > 100) throw new InvalidDataException("Too many imported encryption keys.");
        // Import permits restore; it does not silently change new-backup encryption settings.
        store.Save("encryption.dat", new EncryptionVault { Keys = keys, ActiveKeyId = old.ActiveKeyId, Enabled = old.Enabled });
    }

    private sealed record RecoveryBundle(int Version, List<ArchiveKey> Keys);
    private static List<ArchiveKey> ReadRecovery(string file, string password)
    {
        if (password.Length is < 12 or > 1024) throw new ArgumentException("Use the recovery export's passphrase of 12–1024 characters.");
        if (new FileInfo(file).Length is < 73 or > 256 * 1024) throw new InvalidDataException("Invalid recovery file size.");
        var input = File.ReadAllBytes(file);
        if (!input.AsSpan(0, 8).SequenceEqual("ABKEY001"u8) || BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(8)) != 600_000)
            throw new InvalidDataException("Unsupported recovery file format.");
        var key = Rfc2898DeriveBytes.Pbkdf2(password, input.AsSpan(12, 32), 600_000, HashAlgorithmName.SHA256, 32);
        var plaintext = new byte[input.Length - 72];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(input.AsSpan(44, 12), input.AsSpan(56, plaintext.Length), input.AsSpan(input.Length - 16), plaintext, input.AsSpan(0, 56));
            var bundle = JsonSerializer.Deserialize<RecoveryBundle>(plaintext, Protocol.Json) ?? throw new InvalidDataException("Empty recovery bundle.");
            if (bundle.Version != 1 || bundle.Keys is null || bundle.Keys.Count is < 1 or > 100 || bundle.Keys.Any(k => k is null) || bundle.Keys.Select(k => k.Id).Distinct().Count() != bundle.Keys.Count)
                throw new InvalidDataException("Invalid recovery bundle.");
            foreach (var item in bundle.Keys) { var bytes = item.Bytes(); CryptographicOperations.ZeroMemory(bytes); }
            return bundle.Keys;
        }
        catch (AuthenticationTagMismatchException) { throw new CryptographicException("The recovery passphrase is incorrect or the recovery file was changed."); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }
}
