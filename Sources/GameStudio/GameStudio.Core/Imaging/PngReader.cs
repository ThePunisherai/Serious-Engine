using System.Buffers.Binary;
using System.IO.Compression;

namespace GameStudio.Core.Imaging;

/// <summary>
/// Minimal PNG decoder covering the 8-bit non-interlaced greyscale and truecolour variants
/// (with or without alpha) that the external upscalers in the modernization pipeline emit.
/// Paletted, 16-bit and interlaced images are rejected rather than silently mis-decoded.
/// </summary>
public static class PngReader
{
    public static RgbaImage Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static RgbaImage Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        Span<byte> signature = stackalloc byte[8];
        if (reader.Read(signature) != 8 || signature[0] != 0x89 || signature[1] != 'P' || signature[2] != 'N' || signature[3] != 'G')
            throw new InvalidDataException("Not a PNG file.");

        int width = 0, height = 0, channels = 0;
        byte bitDepth = 0, colorType = 0;
        var idat = new MemoryStream();

        while (stream.Position < stream.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(reader.ReadBytes(4));
            string type = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
            byte[] data = reader.ReadBytes(length);
            _ = reader.ReadBytes(4); // CRC; the container is our own temp file, integrity is not at risk

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4, 4));
                    bitDepth = data[8];
                    colorType = data[9];
                    if (bitDepth != 8)
                        throw new InvalidDataException($"Only 8-bit PNGs are supported, got {bitDepth}-bit.");
                    if (data[12] != 0)
                        throw new InvalidDataException("Interlaced PNGs are not supported.");
                    channels = colorType switch
                    {
                        0 => 1, // greyscale
                        2 => 3, // truecolour
                        4 => 2, // greyscale + alpha
                        6 => 4, // truecolour + alpha
                        _ => throw new InvalidDataException($"Unsupported PNG colour type {colorType}."),
                    };
                    break;

                case "IDAT":
                    idat.Write(data);
                    break;

                case "IEND":
                    goto decode;
            }
        }

    decode:
        if (width <= 0 || height <= 0) throw new InvalidDataException("PNG has no IHDR chunk.");

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);

        return Unfilter(raw.ToArray(), width, height, channels);
    }

    private static RgbaImage Unfilter(byte[] filtered, int width, int height, int channels)
    {
        int stride = width * channels;
        long required = (long)(stride + 1) * height;
        if (filtered.Length < required)
            throw new InvalidDataException($"Truncated PNG data: expected {required} bytes, got {filtered.Length}.");

        var current = new byte[stride];
        var previous = new byte[stride];
        var image = new RgbaImage(width, height);

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (stride + 1);
            byte filter = filtered[rowStart];
            Array.Copy(filtered, rowStart + 1, current, 0, stride);

            for (int i = 0; i < stride; i++)
            {
                byte left = i >= channels ? current[i - channels] : (byte)0;
                byte up = previous[i];
                byte upLeft = i >= channels ? previous[i - channels] : (byte)0;
                current[i] = filter switch
                {
                    0 => current[i],
                    1 => (byte)(current[i] + left),
                    2 => (byte)(current[i] + up),
                    3 => (byte)(current[i] + (byte)((left + up) >> 1)),
                    4 => (byte)(current[i] + Paeth(left, up, upLeft)),
                    _ => throw new InvalidDataException($"Unknown PNG filter type {filter}."),
                };
            }

            WriteRow(image, y, current, channels);
            (previous, current) = (current, previous);
        }

        return image;
    }

    private static void WriteRow(RgbaImage image, int y, byte[] row, int channels)
    {
        for (int x = 0; x < image.Width; x++)
        {
            int s = x * channels;
            byte r, g, b, a;
            switch (channels)
            {
                case 1: r = g = b = row[s]; a = 255; break;
                case 2: r = g = b = row[s]; a = row[s + 1]; break;
                case 3: r = row[s]; g = row[s + 1]; b = row[s + 2]; a = 255; break;
                default: r = row[s]; g = row[s + 1]; b = row[s + 2]; a = row[s + 3]; break;
            }
            image.SetPixel(x, y, r, g, b, a);
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }
}
