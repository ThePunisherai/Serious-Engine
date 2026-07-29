using System.Buffers.Binary;
using System.IO.Compression;

namespace GameStudio.Core.Imaging;

/// <summary>
/// Minimal PNG encoder. Written by hand so GameStudio.Core stays free of native image
/// dependencies and runs identically under the WPF app, the MCP server and CI on Linux.
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static void Save(RgbaImage image, string path, bool dropAlpha = false)
    {
        using var stream = File.Create(path);
        Write(image, stream, dropAlpha);
    }

    public static byte[] ToBytes(RgbaImage image, bool dropAlpha = false)
    {
        using var buffer = new MemoryStream();
        Write(image, buffer, dropAlpha);
        return buffer.ToArray();
    }

    public static void Write(RgbaImage image, Stream output, bool dropAlpha = false)
    {
        bool rgbOnly = dropAlpha || image.IsOpaque();
        int channels = rgbOnly ? 3 : 4;

        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), image.Height);
        header[8] = 8;                          // bit depth
        header[9] = (byte)(rgbOnly ? 2 : 6);    // colour type: truecolour / truecolour+alpha
        header[10] = 0;                         // deflate
        header[11] = 0;                         // adaptive filtering
        header[12] = 0;                         // no interlace
        WriteChunk(output, "IHDR", header);

        byte[] compressed = Compress(Filter(image, channels));
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", []);
    }

    /// <summary>
    /// Applies PNG's per-scanline adaptive filtering, picking the filter with the smallest sum of
    /// absolute differences — the heuristic the spec recommends, and worth a lot on the flat
    /// gradients that dominate generated normal and roughness maps.
    /// </summary>
    private static byte[] Filter(RgbaImage image, int channels)
    {
        int rawStride = image.Width * channels;
        var output = new byte[(rawStride + 1) * image.Height];
        var current = new byte[rawStride];
        var previous = new byte[rawStride];
        var candidate = new byte[rawStride];

        for (int y = 0; y < image.Height; y++)
        {
            PackScanline(image, y, channels, current);

            int bestFilter = 0;
            long bestScore = long.MaxValue;
            int outputRow = y * (rawStride + 1);

            for (int filter = 0; filter <= 4; filter++)
            {
                long score = 0;
                for (int i = 0; i < rawStride; i++)
                {
                    byte left = i >= channels ? current[i - channels] : (byte)0;
                    byte up = previous[i];
                    byte upLeft = i >= channels ? previous[i - channels] : (byte)0;
                    byte value = filter switch
                    {
                        0 => current[i],
                        1 => (byte)(current[i] - left),
                        2 => (byte)(current[i] - up),
                        3 => (byte)(current[i] - (byte)((left + up) >> 1)),
                        _ => (byte)(current[i] - Paeth(left, up, upLeft)),
                    };
                    candidate[i] = value;
                    score += value < 128 ? value : 256 - value;
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    bestFilter = filter;
                    candidate.CopyTo(output.AsSpan(outputRow + 1, rawStride));
                }
            }

            output[outputRow] = (byte)bestFilter;
            (previous, current) = (current, previous);
        }

        return output;
    }

    private static void PackScanline(RgbaImage image, int y, int channels, byte[] destination)
    {
        int source = y * image.Stride;
        if (channels == 4)
        {
            image.Pixels.AsSpan(source, image.Stride).CopyTo(destination);
            return;
        }
        for (int x = 0, d = 0; x < image.Width; x++, source += 4, d += 3)
        {
            destination[d] = image.Pixels[source];
            destination[d + 1] = image.Pixels[source + 1];
            destination[d + 2] = image.Pixels[source + 2];
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static byte[] Compress(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(data);
        return buffer.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        output.Write(typeBytes);
        output.Write(data);

        uint crc = Crc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in first) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in second) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
