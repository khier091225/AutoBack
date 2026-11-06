using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace AutoBack.Legacy;

// Only the net48 client compiles these adapters. Shared backup and wire formats stay identical.
internal static class LegacyConvert
{
    public static string ToHexString(ReadOnlySpan<byte> bytes) => BitConverter.ToString(bytes.ToArray()).Replace("-", "");
    public static byte[] FromHexString(string value)
    {
        if (value.Length % 2 != 0) throw new FormatException("Invalid hex length.");
        var result = new byte[value.Length / 2];
        for (var i = 0; i < result.Length; i++) result[i] = System.Convert.ToByte(value.Substring(i * 2, 2), 16);
        return result;
    }
    public static string ToBase64String(byte[] bytes) => System.Convert.ToBase64String(bytes);
    public static byte[] FromBase64String(string value) => System.Convert.FromBase64String(value);
}
internal static class LegacyPath
{
    public static readonly char DirectorySeparatorChar = System.IO.Path.DirectorySeparatorChar;
    public static string Combine(params string[] parts) => System.IO.Path.Combine(parts);
    public static string GetFullPath(string path) => System.IO.Path.GetFullPath(path);
    public static string? GetDirectoryName(string path) => System.IO.Path.GetDirectoryName(path);
    public static string GetFileName(string path) => System.IO.Path.GetFileName(path);
    public static string GetExtension(string path) => System.IO.Path.GetExtension(path);
    public static string? GetPathRoot(string path) => System.IO.Path.GetPathRoot(path);
    public static char[] GetInvalidFileNameChars() => System.IO.Path.GetInvalidFileNameChars();
    public static bool EndsInDirectorySeparator(string path) => path.Length > 0 && path[path.Length - 1] is '\\' or '/';
    public static bool IsPathFullyQualified(string path) => !string.IsNullOrEmpty(path) &&
        ((path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/') || path.StartsWith(@"\\", StringComparison.Ordinal));
    public static string TrimEndingDirectorySeparator(string path)
        => EndsInDirectorySeparator(path) && path.Length > (GetPathRoot(path)?.Length ?? 0) ? path.Substring(0, path.Length - 1) : path;
    public static string GetRelativePath(string root, string path)
    {
        root = TrimEndingDirectorySeparator(GetFullPath(root)); path = GetFullPath(path);
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return ".";
        var prefix = root + (EndsInDirectorySeparator(root) ? "" : "\\");
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path is outside the selected source.");
        return path.Substring(prefix.Length);
    }
}
internal static class LegacyFile
{
    public static bool Exists(string? path) => System.IO.File.Exists(path);
    public static void Delete(string path) => System.IO.File.Delete(path);
    public static string ReadAllText(string path) => System.IO.File.ReadAllText(path);
    public static byte[] ReadAllBytes(string path) => System.IO.File.ReadAllBytes(path);
    public static void WriteAllText(string path, string text) => System.IO.File.WriteAllText(path, text);
    public static FileAttributes GetAttributes(string path) => System.IO.File.GetAttributes(path);
    public static DateTime GetLastWriteTimeUtc(string path) => System.IO.File.GetLastWriteTimeUtc(path);
    public static void SetLastWriteTimeUtc(string path, DateTime time) => System.IO.File.SetLastWriteTimeUtc(path, time);
    public static FileStream OpenRead(string path) => System.IO.File.OpenRead(path);
    public static Task<string> ReadAllTextAsync(string path, CancellationToken token = default)
        => Task.Run(() => { token.ThrowIfCancellationRequested(); return System.IO.File.ReadAllText(path); }, token);
    public static void Move(string source, string destination, bool overwrite = false)
    {
        // File.Replace preserves atomic replacement on NTFS; never delete the old journal first.
        if (overwrite && Exists(destination)) System.IO.File.Replace(source, destination, null);
        else System.IO.File.Move(source, destination);
    }
}
internal static class LegacyRandomNumberGenerator
{
    public static byte[] GetBytes(int count) { var bytes = new byte[count]; using var rng = System.Security.Cryptography.RandomNumberGenerator.Create(); rng.GetBytes(bytes); return bytes; }
    public static void Fill(Span<byte> bytes) { var data = GetBytes(bytes.Length); try { data.AsSpan().CopyTo(bytes); } finally { LegacyCryptographicOperations.ZeroMemory(data); } }
}
internal static class LegacySha256
{
    public static byte[] HashData(ReadOnlySpan<byte> bytes) { using var hash = System.Security.Cryptography.SHA256.Create(); return hash.ComputeHash(bytes.ToArray()); }
    public static byte[] HashData(Stream input) { using var hash = System.Security.Cryptography.SHA256.Create(); return hash.ComputeHash(input); }
    public static async Task<byte[]> HashDataAsync(Stream input, CancellationToken token = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[128 * 1024]; int count;
        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0) hash.AppendData(buffer, 0, count);
        return hash.GetHashAndReset();
    }
}
internal static class LegacyMd5
{
    public static byte[] HashData(byte[] bytes) { using var hash = System.Security.Cryptography.MD5.Create(); return hash.ComputeHash(bytes); }
    public static async Task<byte[]> HashDataAsync(Stream input, CancellationToken token = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5); var buffer = new byte[128 * 1024]; int count;
        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0) hash.AppendData(buffer, 0, count);
        return hash.GetHashAndReset();
    }
}
internal static class LegacyCryptographicOperations
{
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static void ZeroMemory(Span<byte> bytes) { for (var i = 0; i < bytes.Length; i++) bytes[i] = 0; }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static bool FixedTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    { if (a.Length != b.Length) return false; var difference = 0; for (var i = 0; i < a.Length; i++) difference |= a[i] ^ b[i]; return difference == 0; }
}
internal sealed class LegacyAuthenticationTagMismatchException : CryptographicException { }
internal sealed class LegacyAesGcm : IDisposable
{
    private readonly byte[] _key;
    public LegacyAesGcm(byte[] key, int tagSizeInBytes) { if (tagSizeInBytes != 16 || key.Length != 32) throw new CryptographicException("Invalid AES-GCM parameters."); _key = (byte[])key.Clone(); }
    public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData)
    {
        if (nonce.Length != 12 || tag.Length != 16 || ciphertext.Length != plaintext.Length) throw new CryptographicException("Invalid AES-GCM lengths.");
        var input = plaintext.ToArray(); var output = new byte[input.Length + 16];
        try { var cipher = new GcmBlockCipher(new AesEngine()); cipher.Init(true, new AeadParameters(new KeyParameter(_key), 128, nonce.ToArray(), associatedData.ToArray()));
            var count = cipher.ProcessBytes(input, 0, input.Length, output, 0); cipher.DoFinal(output, count);
            output.AsSpan(0, input.Length).CopyTo(ciphertext); output.AsSpan(input.Length, 16).CopyTo(tag); }
        finally { LegacyCryptographicOperations.ZeroMemory(input); LegacyCryptographicOperations.ZeroMemory(output); }
    }
    public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        if (nonce.Length != 12 || tag.Length != 16 || ciphertext.Length != plaintext.Length) throw new CryptographicException("Invalid AES-GCM lengths.");
        var input = new byte[ciphertext.Length + 16]; ciphertext.CopyTo(input); tag.CopyTo(input.AsSpan(ciphertext.Length)); var output = new byte[ciphertext.Length];
        try { var cipher = new GcmBlockCipher(new AesEngine()); cipher.Init(false, new AeadParameters(new KeyParameter(_key), 128, nonce.ToArray(), associatedData.ToArray()));
            var count = cipher.ProcessBytes(input, 0, input.Length, output, 0); cipher.DoFinal(output, count); output.AsSpan().CopyTo(plaintext); }
        catch (InvalidCipherTextException) { plaintext.Clear(); throw new LegacyAuthenticationTagMismatchException(); }
        finally { LegacyCryptographicOperations.ZeroMemory(input); LegacyCryptographicOperations.ZeroMemory(output); }
    }
    public void Dispose() => LegacyCryptographicOperations.ZeroMemory(_key);
}
internal static class LegacyHkdf
{
    public static byte[] DeriveKey(HashAlgorithmName algorithm, byte[] input, int length, byte[] salt, byte[] info)
    {
        if (algorithm != HashAlgorithmName.SHA256) throw new CryptographicException("Unsupported HKDF algorithm.");
        var generator = new HkdfBytesGenerator(new Org.BouncyCastle.Crypto.Digests.Sha256Digest()); generator.Init(new HkdfParameters(input, salt, info));
        var result = new byte[length]; generator.GenerateBytes(result, 0, length); return result;
    }
}
internal static class LegacyPbkdf2
{
    public static byte[] Pbkdf2(string password, ReadOnlySpan<byte> salt, int iterations, HashAlgorithmName algorithm, int outputLength)
    { using var derive = new System.Security.Cryptography.Rfc2898DeriveBytes(password, salt.ToArray(), iterations, algorithm); return derive.GetBytes(outputLength); }
}
internal static class LegacyRandom { public static readonly System.Random Shared = new(); }
internal sealed class LegacyTcpListener(System.Net.IPAddress address, int port) : System.Net.Sockets.TcpListener(address, port), IDisposable
{ public void Dispose() => Stop(); }

internal static class FrameworkExtensions
{
    public static IEnumerable<T> TakeLast<T>(this IEnumerable<T> values, int count) { var queue = new Queue<T>(); foreach (var value in values) { queue.Enqueue(value); if (queue.Count > count) queue.Dequeue(); } return queue; }
    public static bool TryPop<T>(this Stack<T> values, out T value) { if (values.Count > 0) { value = values.Pop(); return true; } value = default!; return false; }
    public static IEnumerable<string> EnumerateRunes(this string text)
    { for (var i = 0; i < text.Length; i++) { var count = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1; yield return text.Substring(i, count); i += count - 1; } }
    public static TValue? GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> values, TKey key) => values.TryGetValue(key, out var value) ? value : default;
    public static bool TryAdd<TKey, TValue>(this IDictionary<TKey, TValue> values, TKey key, TValue value) { if (values.ContainsKey(key)) return false; values.Add(key, value); return true; }
    public static bool Contains(this string text, char value) => text.IndexOf(value) >= 0;
    public static bool StartsWith(this string text, char value) => text.Length > 0 && text[0] == value;
    public static bool EndsWith(this string text, char value) => text.Length > 0 && text[text.Length - 1] == value;
    public static bool Contains(this string text, string value, StringComparison comparison) => text.IndexOf(value, comparison) >= 0;
    public static string[] Split(this string text, string separator) => text.Split(new[] { separator }, StringSplitOptions.None);
    public static string[] Split(this string text, char separator, StringSplitOptions options) => text.Split(new[] { separator }, options);
    public static string[] Split(this string text, char separator, int count) => text.Split(new[] { separator }, count);
    public static bool TryWriteBytes(this Guid guid, Span<byte> bytes) { if (bytes.Length < 16) return false; guid.ToByteArray().AsSpan().CopyTo(bytes); return true; }
    public static void AppendData(this IncrementalHash hash, ReadOnlySpan<byte> bytes) => hash.AppendData(bytes.ToArray());
    public static ValueTask DisposeAsync(this Stream stream) { stream.Dispose(); return default; }
    public static void Write(this Stream stream, ReadOnlySpan<byte> bytes) { var copy = bytes.ToArray(); stream.Write(copy, 0, copy.Length); }
    public static int Read(this Stream stream, Span<byte> bytes) { var copy = new byte[bytes.Length]; var count = stream.Read(copy, 0, copy.Length); copy.AsSpan(0, count).CopyTo(bytes); return count; }
    public static int ReadAtLeast(this Stream stream, Span<byte> bytes, int minimumBytes, bool throwOnEndOfStream = true)
    {
        var total = 0; while (total < minimumBytes) { var count = Read(stream, bytes.Slice(total)); if (count == 0) { if (throwOnEndOfStream) throw new EndOfStreamException(); break; } total += count; } return total;
    }
    public static void ReadExactly(this Stream stream, Span<byte> bytes) => ReadAtLeast(stream, bytes, bytes.Length);
    public static async Task ReadExactlyAsync(this Stream stream, Memory<byte> bytes, CancellationToken token = default)
    { var total = 0; while (total < bytes.Length) { var count = await stream.ReadAsync(bytes.Slice(total), token); if (count == 0) throw new EndOfStreamException(); total += count; } }
    public static async ValueTask<int> ReadAsync(this Stream stream, Memory<byte> bytes, CancellationToken token = default)
    { var copy = new byte[bytes.Length]; var count = await stream.ReadAsync(copy, 0, copy.Length, token); copy.AsMemory(0, count).CopyTo(bytes); return count; }
    public static Task WriteAsync(this Stream stream, ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    { var copy = bytes.ToArray(); return stream.WriteAsync(copy, 0, copy.Length, token); }
    public static Task<string> ReadAsStringAsync(this HttpContent content, CancellationToken token) { token.ThrowIfCancellationRequested(); return content.ReadAsStringAsync(); }
    public static Task<byte[]> ReadAsByteArrayAsync(this HttpContent content, CancellationToken token) { token.ThrowIfCancellationRequested(); return content.ReadAsByteArrayAsync(); }
    public static Task<Stream> ReadAsStreamAsync(this HttpContent content, CancellationToken token) { token.ThrowIfCancellationRequested(); return content.ReadAsStreamAsync(); }
    public static Task CancelAsync(this CancellationTokenSource source) { source.Cancel(); return Task.CompletedTask; }
    public static async Task<TcpClient> AcceptTcpClientAsync(this TcpListener listener, CancellationToken token)
    {
        using var cancel = token.Register(() => listener.Stop());
        try { return await listener.AcceptTcpClientAsync(); } catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }
}
