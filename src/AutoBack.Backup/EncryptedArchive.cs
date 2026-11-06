using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AutoBack.Backup;

/// <summary>Versioned, bounded-memory authenticated envelope around an ordinary AutoBack ZIP.</summary>
public static class ArchiveFile
{
    public const int ChunkSize = 1024 * 1024;
    internal const int HeaderSize = 64, FooterSize = 32;
    internal static ReadOnlySpan<byte> Magic => "AUTOBK01"u8;

    public static Stream OpenRead(string path, Func<Guid, ArchiveKey>? keys = null)
    {
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.RandomAccess);
        try
        {
            Span<byte> magic = stackalloc byte[8];
            var count = input.ReadAtLeast(magic, 8, throwOnEndOfStream: false); input.Position = 0;
            if (count != 8 || !magic.SequenceEqual(Magic)) return input;
            return new EncryptedReadStream(input, keys ?? (_ => throw new CryptographicException("Import this encrypted backup's recovery key before restoring.")));
        }
        catch { input.Dispose(); throw; }
    }

    public static async Task AuthenticateAsync(Stream input, CancellationToken token = default)
    {
        if (input is not EncryptedReadStream) return;
        var position = input.Position;
        var buffer = new byte[ChunkSize];
        try
        {
            input.Position = 0;
            while (await input.ReadAsync(buffer, token) > 0) { }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); input.Position = position; }
    }

    internal static byte[] Derive(ArchiveKey key, ReadOnlySpan<byte> header)
    {
        var master = key.Bytes();
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, header.Slice(32, 32).ToArray(), "AutoBack archive v1"u8.ToArray()); }
        finally { CryptographicOperations.ZeroMemory(master); }
    }
    internal static byte[] Nonce(ulong chunk)
    {
        var nonce = new byte[12]; BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), chunk); return nonce;
    }
    internal static byte[] FooterAad(byte[] header, long length)
    {
        var aad = new byte[72]; header.CopyTo(aad, 0); BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(64), length); return aad;
    }
}

public sealed class EncryptedWriteStream : Stream
{
    private readonly Stream _output;
    private readonly AesGcm _aes;
    private readonly byte[] _header = new byte[64], _plain = new byte[ArchiveFile.ChunkSize];
    private int _buffered;
    private long _length;
    private ulong _chunk;
    private bool _finished;
    public EncryptedWriteStream(Stream output, ArchiveKey key)
    {
        _output = output;
        ArchiveFile.Magic.CopyTo(_header);
        BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(12), ArchiveFile.ChunkSize);
        key.Id.TryWriteBytes(_header.AsSpan(16, 16)); RandomNumberGenerator.Fill(_header.AsSpan(32));
        var derived = ArchiveFile.Derive(key, _header);
        try { _aes = new AesGcm(derived, 16); } finally { CryptographicOperations.ZeroMemory(derived); }
        try { output.Write(_header); } catch { _aes.Dispose(); throw; }
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
#if NETFRAMEWORK
    public void Write(ReadOnlySpan<byte> buffer)
#else
    public override void Write(ReadOnlySpan<byte> buffer)
#endif
    {
        if (_finished) throw new InvalidOperationException("Encrypted archive is already complete.");
        while (!buffer.IsEmpty)
        {
            var count = Math.Min(_plain.Length - _buffered, buffer.Length);
            buffer[..count].CopyTo(_plain.AsSpan(_buffered)); _buffered += count; _length = checked(_length + count); buffer = buffer[count..];
            if (_length > 1L << 40) throw new IOException("The encrypted archive exceeds the 1 TiB format limit.");
            if (_buffered == _plain.Length) WriteChunk();
        }
    }
#if NETFRAMEWORK
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
#else
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
#endif
    { token.ThrowIfCancellationRequested(); Write(buffer.Span); return default; }
    private void WriteChunk()
    {
        var encrypted = new byte[_buffered]; Span<byte> tag = stackalloc byte[16];
        _aes.Encrypt(ArchiveFile.Nonce(_chunk++), _plain.AsSpan(0, _buffered), encrypted, tag, _header);
        _output.Write(encrypted); _output.Write(tag); CryptographicOperations.ZeroMemory(_plain); _buffered = 0;
    }
    public void Finish()
    {
        if (_finished) return;
        if (_buffered > 0) WriteChunk();
        Span<byte> footer = stackalloc byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(footer, _length);
        _aes.Encrypt(ArchiveFile.Nonce(ulong.MaxValue), [], [], footer.Slice(8, 16), ArchiveFile.FooterAad(_header, _length));
        "ABKEND01"u8.CopyTo(footer[24..]); _output.Write(footer); _finished = true;
    }
    public override void Flush() => _output.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_finished;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { _aes.Dispose(); CryptographicOperations.ZeroMemory(_plain); } base.Dispose(disposing); }
}

internal sealed class EncryptedReadStream : Stream
{
    private readonly FileStream _input;
    private readonly AesGcm _aes;
    private readonly byte[] _header = new byte[64], _plain = new byte[ArchiveFile.ChunkSize];
    private long _position, _cached = -1;
    public override long Length { get; }
    public EncryptedReadStream(FileStream input, Func<Guid, ArchiveKey> keys)
    {
        _input = input;
        if (input.Length < ArchiveFile.HeaderSize + ArchiveFile.FooterSize) throw new InvalidDataException("Encrypted archive is truncated.");
        input.ReadExactly(_header);
        if (BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(8)) != 1 || BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(12)) != ArchiveFile.ChunkSize)
            throw new InvalidDataException("Unsupported encrypted archive format.");
        var key = keys(new Guid(_header.AsSpan(16, 16).ToArray()));
        var derived = ArchiveFile.Derive(key, _header);
        try { _aes = new AesGcm(derived, 16); } finally { CryptographicOperations.ZeroMemory(derived); }
        try
        {
            input.Position = input.Length - 32;
            Span<byte> footer = stackalloc byte[32]; input.ReadExactly(footer);
            Length = BinaryPrimitives.ReadInt64LittleEndian(footer);
            if (Length is < 0 or > (1L << 40) || !footer[24..].SequenceEqual("ABKEND01"u8)) throw new InvalidDataException("Invalid encrypted archive footer.");
            _aes.Decrypt(ArchiveFile.Nonce(ulong.MaxValue), [], footer.Slice(8, 16), [], ArchiveFile.FooterAad(_header, Length));
            var chunks = (Length + ArchiveFile.ChunkSize - 1) / ArchiveFile.ChunkSize;
            if (input.Length != 96 + Length + 16 * chunks) throw new InvalidDataException("Encrypted archive is truncated or has unexpected trailing content.");
        }
        catch { _aes.Dispose(); throw; }
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
#if NETFRAMEWORK
    public int Read(Span<byte> buffer)
#else
    public override int Read(Span<byte> buffer)
#endif
    {
        var copied = 0;
        Span<byte> tag = stackalloc byte[16];
        while (!buffer.IsEmpty && _position < Length)
        {
            var chunk = _position / ArchiveFile.ChunkSize;
            var size = (int)Math.Min(ArchiveFile.ChunkSize, Length - chunk * ArchiveFile.ChunkSize);
            if (_cached != chunk)
            {
                _input.Position = 64 + chunk * (ArchiveFile.ChunkSize + 16L);
                var ciphertext = new byte[size]; _input.ReadExactly(ciphertext);
                _input.ReadExactly(tag);
                _aes.Decrypt(ArchiveFile.Nonce((ulong)chunk), ciphertext, tag, _plain.AsSpan(0, size), _header);
                _cached = chunk;
            }
            var offset = (int)(_position % ArchiveFile.ChunkSize);
            var take = Math.Min(buffer.Length, size - offset);
            _plain.AsSpan(offset, take).CopyTo(buffer); buffer = buffer[take..]; copied += take; _position += take;
        }
        return copied;
    }
#if NETFRAMEWORK
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
#else
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
#endif
    { token.ThrowIfCancellationRequested(); return new(Read(buffer.Span)); }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var value = checked(origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, SeekOrigin.End => Length + offset, _ => throw new ArgumentException("Invalid seek origin.") });
        if (value < 0) throw new IOException("Cannot seek before the archive."); return _position = value;
    }
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    { if (disposing) { _aes.Dispose(); _input.Dispose(); CryptographicOperations.ZeroMemory(_plain); } base.Dispose(disposing); }
}
