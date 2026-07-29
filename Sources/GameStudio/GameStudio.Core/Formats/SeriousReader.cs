using System.Text;

namespace GameStudio.Core.Formats;

/// <summary>
/// Reader for the chunked little-endian container that Serious Engine 1 uses for its asset
/// files. Mirrors CTStream from Sources/Engine/Base/Stream.cpp: chunk identifiers are four raw
/// ASCII bytes (CID_LENGTH), and INDEX/ULONG are 32-bit little-endian.
/// </summary>
public sealed class SeriousReader : IDisposable
{
    public const int ChunkIdLength = 4;

    private readonly BinaryReader _reader;
    private readonly bool _ownsStream;

    public SeriousReader(Stream stream, bool leaveOpen = false)
    {
        _reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        _ownsStream = !leaveOpen;
    }

    public Stream BaseStream => _reader.BaseStream;
    public long Position => _reader.BaseStream.Position;
    public long Length => _reader.BaseStream.Length;
    public bool AtEnd => Position >= Length;

    public int ReadIndex() => _reader.ReadInt32();
    public uint ReadUlong() => _reader.ReadUInt32();
    public float ReadFloat() => _reader.ReadSingle();
    public byte[] ReadBytes(int count) => _reader.ReadBytes(count);

    public void Skip(long count) => _reader.BaseStream.Seek(count, SeekOrigin.Current);
    public void Seek(long position) => _reader.BaseStream.Seek(position, SeekOrigin.Begin);

    /// <summary>Reads a four-byte chunk identifier, or null when fewer than four bytes remain.</summary>
    public string? ReadChunkId()
    {
        if (Length - Position < ChunkIdLength) return null;
        return Encoding.ASCII.GetString(_reader.ReadBytes(ChunkIdLength));
    }

    /// <summary>Reads a chunk identifier and throws unless it matches <paramref name="expected"/>.</summary>
    public void ExpectChunkId(string expected)
    {
        string? actual = ReadChunkId();
        if (actual != expected)
            throw new InvalidDataException($"Expected chunk '{expected}' at offset {Position - ChunkIdLength}, found '{actual ?? "<eof>"}'.");
    }

    /// <summary>Peeks the next chunk identifier without consuming it.</summary>
    public string? PeekChunkId()
    {
        long start = Position;
        string? id = ReadChunkId();
        Seek(start);
        return id;
    }

    /// <summary>
    /// Reads a CTString: a 32-bit length followed by that many raw bytes, with no terminator.
    /// </summary>
    public string ReadString()
    {
        int length = ReadIndex();
        if (length < 0 || length > Length - Position)
            throw new InvalidDataException($"Implausible string length {length} at offset {Position - 4}.");
        return Encoding.Latin1.GetString(_reader.ReadBytes(length));
    }

    public void Dispose()
    {
        _reader.Dispose();
        if (_ownsStream) BaseStream.Dispose();
    }
}
