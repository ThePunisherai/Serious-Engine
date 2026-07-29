using GameStudio.Core.Imaging;

namespace GameStudio.Core.Formats;

[Flags]
public enum TexFlags : uint
{
    None = 0,
    AlphaChannel = 1u << 0,
    Bit32 = 1u << 1,
    Static = 1u << 5,
    Constant = 1u << 6,
    Transparent = 1u << 7,
    Equalized = 1u << 8,
    Gray = 1u << 9,
    White = 1u << 10,
    KeepColor = 1u << 11,
}

/// <summary>
/// Decoder for Serious Engine 1 <c>.tex</c> files, matching CTextureData::Read_t in
/// Sources/Engine/Graphics/Texture.cpp.
/// <para>
/// Two on-disk revisions exist. Version 4 stores a single full-resolution frame as raw 24- or
/// 32-bit RGB(A). Version 3 stores a whole mipmap chain per frame at 16 bits per texel, packed
/// as RGBA4444 when the alpha flag is set and RGB5551 otherwise; the engine unpacks only the
/// largest mip and regenerates the rest, and so does this reader.
/// </para>
/// <para>
/// Effect textures (fire, water, plasma) carry an FXDT chunk instead of pixel data — their
/// frames are simulated at runtime — so <see cref="Frames"/> is legitimately empty for them.
/// </para>
/// </summary>
public sealed class TexFile
{
    private TexFile(int version, TexFlags flags, int mexWidth, int mexHeight, int fineMipLevels,
        int firstMipLevel, int declaredFrameCount, IReadOnlyList<RgbaImage> frames, uint? effectType)
    {
        Version = version;
        Flags = flags;
        MexWidth = mexWidth;
        MexHeight = mexHeight;
        FineMipLevels = fineMipLevels;
        FirstMipLevel = firstMipLevel;
        DeclaredFrameCount = declaredFrameCount;
        Frames = frames;
        EffectType = effectType;
    }

    public int Version { get; }
    public TexFlags Flags { get; }

    /// <summary>Width in engine "mex" units, before the first-mip shift is applied.</summary>
    public int MexWidth { get; }
    public int MexHeight { get; }
    public int FineMipLevels { get; }
    public int FirstMipLevel { get; }

    /// <summary>Frame count from the header, which effect textures declare without storing pixels.</summary>
    public int DeclaredFrameCount { get; }

    /// <summary>Decoded frames at full resolution. Empty for effect textures.</summary>
    public IReadOnlyList<RgbaImage> Frames { get; }

    /// <summary>Effect class from the FXDT chunk, or null for an ordinary texture.</summary>
    public uint? EffectType { get; }

    public bool IsEffectTexture => EffectType.HasValue;
    public int PixelWidth => MexWidth >> FirstMipLevel;
    public int PixelHeight => MexHeight >> FirstMipLevel;
    public bool HasAlpha => Flags.HasFlag(TexFlags.AlphaChannel);
    public bool IsAnimated => Frames.Count > 1;

    public static TexFile Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static TexFile Load(Stream stream)
    {
        using var reader = new SeriousReader(stream, leaveOpen: true);

        reader.ExpectChunkId("TVER");
        int version = reader.ReadIndex();
        if (version is not (3 or 4))
            throw new InvalidDataException($"Unsupported texture format version {version}; expected 3 or 4.");

        TexFlags flags = TexFlags.None;
        int mexWidth = 0, mexHeight = 0, fineMipLevels = 0, firstMipLevel = 0, frameCount = 0, legacyFrameSize = 0;
        var frames = new List<RgbaImage>();
        uint? effectType = null;

        while (true)
        {
            string? chunk = reader.ReadChunkId();
            // The engine treats a blank identifier as a terminator, and stops at end of file.
            if (chunk is null || chunk == "    ") break;

            if (chunk == "TDAT")
            {
                flags = (TexFlags)reader.ReadUlong();
                mexWidth = reader.ReadIndex();
                mexHeight = reader.ReadIndex();
                fineMipLevels = reader.ReadIndex();
                if (version != 4) _ = reader.ReadIndex();          // total mip count, recomputed on load
                firstMipLevel = reader.ReadIndex();
                if (version != 4) legacyFrameSize = reader.ReadIndex();
                frameCount = reader.ReadIndex();
                ValidateHeader(mexWidth, mexHeight, firstMipLevel, frameCount);
            }
            else if (chunk == "FRMS")
            {
                frames.AddRange(ReadFrames(reader, version, flags, mexWidth >> firstMipLevel,
                    mexHeight >> firstMipLevel, frameCount, legacyFrameSize));
            }
            else if (chunk == "FXDT")
            {
                // An effect texture's pixels are simulated at runtime; the rest of the file
                // describes emitters, which this reader has no use for.
                effectType = reader.ReadUlong();
                break;
            }
            else
            {
                // ANIM, BAST and the legacy effect-buffer chunks are variable length and always
                // trail the pixel data, so decoding is complete once one shows up.
                break;
            }
        }

        return new TexFile(version, flags, mexWidth, mexHeight, fineMipLevels, firstMipLevel,
            frameCount, frames, effectType);
    }

    private static void ValidateHeader(int mexWidth, int mexHeight, int firstMipLevel, int frameCount)
    {
        if (mexWidth <= 0 || mexHeight <= 0)
            throw new InvalidDataException($"Invalid texture dimensions {mexWidth}x{mexHeight}.");
        if (firstMipLevel < 0 || firstMipLevel > 30)
            throw new InvalidDataException($"Invalid first mip level {firstMipLevel}.");
        if ((mexWidth >> firstMipLevel) <= 0 || (mexHeight >> firstMipLevel) <= 0)
            throw new InvalidDataException($"First mip level {firstMipLevel} collapses {mexWidth}x{mexHeight} to zero.");
        if (frameCount is <= 0 or > 4096)
            throw new InvalidDataException($"Implausible frame count {frameCount}.");
    }

    private static List<RgbaImage> ReadFrames(SeriousReader reader, int version, TexFlags flags,
        int pixelWidth, int pixelHeight, int frameCount, int legacyFrameSize)
    {
        return version == 4
            ? ReadModernFrames(reader, flags, pixelWidth, pixelHeight, frameCount)
            : ReadLegacyFrames(reader, flags, pixelWidth, pixelHeight, frameCount, legacyFrameSize);
    }

    /// <summary>Version 4: one full-resolution frame after another, 24- or 32-bit RGB(A).</summary>
    private static List<RgbaImage> ReadModernFrames(SeriousReader reader, TexFlags flags,
        int pixelWidth, int pixelHeight, int frameCount)
    {
        bool hasAlpha = flags.HasFlag(TexFlags.AlphaChannel);
        int bytesPerPixel = hasAlpha ? 4 : 3;
        int pixelCount = pixelWidth * pixelHeight;
        int frameBytes = pixelCount * bytesPerPixel;

        var frames = new List<RgbaImage>(frameCount);
        for (int frame = 0; frame < frameCount; frame++)
        {
            byte[] raw = reader.ReadBytes(frameBytes);
            if (raw.Length != frameBytes)
                throw new InvalidDataException($"Truncated frame {frame}: wanted {frameBytes} bytes, got {raw.Length}.");
            frames.Add(hasAlpha ? new RgbaImage(pixelWidth, pixelHeight, raw) : ExpandRgbToRgba(raw, pixelWidth, pixelHeight));
        }
        return frames;
    }

    /// <summary>
    /// Version 3: each frame is a complete 16-bit mipmap chain of <c>legacyFrameSize</c> bytes.
    /// Only the largest mip carries information the engine keeps, so the remainder is skipped.
    /// </summary>
    private static List<RgbaImage> ReadLegacyFrames(SeriousReader reader, TexFlags flags,
        int pixelWidth, int pixelHeight, int frameCount, int legacyFrameSize)
    {
        bool hasAlpha = flags.HasFlag(TexFlags.AlphaChannel);
        int pixelCount = pixelWidth * pixelHeight;
        int largestMipBytes = pixelCount * 2;

        int frameStride = legacyFrameSize > 0 ? legacyFrameSize : MipChainTexels(pixelWidth, pixelHeight) * 2;
        if (frameStride < largestMipBytes)
            throw new InvalidDataException($"Frame stride {frameStride} is smaller than the {largestMipBytes}-byte base mip.");

        var frames = new List<RgbaImage>(frameCount);
        for (int frame = 0; frame < frameCount; frame++)
        {
            byte[] raw = reader.ReadBytes(largestMipBytes);
            if (raw.Length != largestMipBytes)
                throw new InvalidDataException($"Truncated frame {frame}: wanted {largestMipBytes} bytes, got {raw.Length}.");

            frames.Add(Unpack16Bit(raw, pixelWidth, pixelHeight, hasAlpha));
            reader.Skip(frameStride - largestMipBytes);
        }
        return frames;
    }

    /// <summary>
    /// Expands RGBA4444 (alpha) or RGB5551 (opaque) to 8 bits per channel, replicating the high
    /// bits into the low ones so full-scale inputs stay full-scale. Mirrors Convert() in Texture.cpp.
    /// </summary>
    private static RgbaImage Unpack16Bit(byte[] raw, int width, int height, bool hasAlpha)
    {
        var image = new RgbaImage(width, height);
        var dst = image.Pixels;

        for (int p = 0, s = 0, d = 0; p < width * height; p++, s += 2, d += 4)
        {
            ushort packed = (ushort)(raw[s] | (raw[s + 1] << 8));
            byte r, g, b, a;

            if (hasAlpha)
            {
                r = (byte)((packed & 0xF000) >> 8);
                g = (byte)((packed & 0x0F00) >> 4);
                b = (byte)(packed & 0x00F0);
                a = (byte)((packed & 0x000F) << 4);
                r |= (byte)(r >> 4); g |= (byte)(g >> 4); b |= (byte)(b >> 4); a |= (byte)(a >> 4);
            }
            else
            {
                r = (byte)((packed & 0xF800) >> 8);
                g = (byte)((packed & 0x07C0) >> 3);
                b = (byte)((packed & 0x003E) << 2);
                a = 0xFF;
                r |= (byte)(r >> 5); g |= (byte)(g >> 5); b |= (byte)(b >> 5);
            }

            dst[d] = r; dst[d + 1] = g; dst[d + 2] = b; dst[d + 3] = a;
        }

        return image;
    }

    private static RgbaImage ExpandRgbToRgba(byte[] rgb, int width, int height)
    {
        var image = new RgbaImage(width, height);
        var dst = image.Pixels;
        for (int p = 0, s = 0, d = 0; p < width * height; p++, s += 3, d += 4)
        {
            dst[d] = rgb[s];
            dst[d + 1] = rgb[s + 1];
            dst[d + 2] = rgb[s + 2];
            dst[d + 3] = 255;
        }
        return image;
    }

    /// <summary>
    /// Total texel count of a full mipmap chain, matching GetMipmapOffset(15, w, h) in
    /// Graphics.cpp: the level count is log2(min(w,h)) + 1 and each level is a quarter of the last.
    /// </summary>
    internal static int MipChainTexels(int width, int height, int maxLevels = 15)
    {
        int levels = Math.Min(maxLevels, MipLevelCount(width, height));
        int total = 0;
        int size = width * height;
        while (levels > 0)
        {
            total += size;
            size >>= 2;
            levels--;
        }
        return total;
    }

    internal static int MipLevelCount(int width, int height)
    {
        int smaller = Math.Min(width, height);
        int levels = 1;
        while (smaller > 1) { smaller >>= 1; levels++; }
        return levels;
    }
}
