using GameStudio.Core.Formats;
using GameStudio.Core.Imaging;

namespace GameStudio.Core.Tests;

public class TexFileTests
{
    [Fact]
    public void Load_RejectsUnsupportedVersion()
    {
        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        writer.Write("TVER"u8.ToArray());
        writer.Write(2);
        stream.Position = 0;

        var ex = Assert.Throws<InvalidDataException>(() => TexFile.Load(stream));
        Assert.Contains("version 2", ex.Message);
    }

    [Fact]
    public void Load_RejectsWrongMagic()
    {
        using var stream = new MemoryStream("NOPE\0\0\0\0"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => TexFile.Load(stream));
    }

    [Fact]
    public void Load_ReadsVersion4OpaqueTexture()
    {
        using var stream = BuildVersion4Texture(width: 4, height: 2, hasAlpha: false, frames: 1);
        var texture = TexFile.Load(stream);

        Assert.Equal(4, texture.Version);
        Assert.Equal(4, texture.PixelWidth);
        Assert.Equal(2, texture.PixelHeight);
        Assert.False(texture.HasAlpha);
        Assert.Single(texture.Frames);

        // Synthetic payload is a red/green ramp; opaque textures get a fully opaque alpha channel.
        var (r, g, b, a) = texture.Frames[0].GetPixel(0, 0);
        Assert.Equal(0, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
        Assert.Equal(255, a);
        Assert.True(texture.Frames[0].IsOpaque());
    }

    [Fact]
    public void Load_ReadsAlphaChannelWhenFlagged()
    {
        using var stream = BuildVersion4Texture(width: 2, height: 2, hasAlpha: true, frames: 1);
        var texture = TexFile.Load(stream);

        Assert.True(texture.HasAlpha);
        Assert.False(texture.Frames[0].IsOpaque());
    }

    [Fact]
    public void Load_ReadsEveryFrameOfAnAnimatedTexture()
    {
        using var stream = BuildVersion4Texture(width: 2, height: 2, hasAlpha: false, frames: 3);
        var texture = TexFile.Load(stream);

        Assert.Equal(3, texture.Frames.Count);
        Assert.True(texture.IsAnimated);
    }

    [Fact]
    public void Load_AppliesFirstMipLevelShiftToDimensions()
    {
        using var stream = BuildVersion4Texture(width: 8, height: 8, hasAlpha: false, frames: 1, firstMipLevel: 1);
        var texture = TexFile.Load(stream);

        Assert.Equal(8, texture.MexWidth);
        Assert.Equal(4, texture.PixelWidth);
        Assert.Equal(4, texture.Frames[0].Width);
    }

    [Fact]
    public void Load_ThrowsOnTruncatedFrameData()
    {
        var full = BuildVersion4Texture(width: 8, height: 8, hasAlpha: false, frames: 1).ToArray();
        using var truncated = new MemoryStream(full[..(full.Length - 32)]);
        Assert.Throws<InvalidDataException>(() => TexFile.Load(truncated));
    }

    [Fact]
    public void Load_RejectsImplausibleFrameCount()
    {
        using var stream = BuildVersion4Texture(width: 2, height: 2, hasAlpha: false, frames: 1, frameCountOverride: 999999);
        Assert.Throws<InvalidDataException>(() => TexFile.Load(stream));
    }

    /// <summary>
    /// Builds a version-4 .tex in memory matching the layout CTextureData::Write_t produces:
    /// "TVER" + version, then a TDAT header chunk, then a FRMS chunk of raw RGB(A) frames.
    /// </summary>
    internal static MemoryStream BuildVersion4Texture(int width, int height, bool hasAlpha, int frames,
        int firstMipLevel = 0, int? frameCountOverride = null)
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);

        writer.Write("TVER"u8.ToArray());
        writer.Write(4);

        writer.Write("TDAT"u8.ToArray());
        writer.Write(hasAlpha ? (uint)TexFlags.AlphaChannel : 0u);
        writer.Write(width);                    // mex width
        writer.Write(height);                   // mex height
        writer.Write(1);                        // fine mip levels
        writer.Write(firstMipLevel);
        writer.Write(frameCountOverride ?? frames);

        writer.Write("FRMS"u8.ToArray());
        int pixelWidth = width >> firstMipLevel;
        int pixelHeight = height >> firstMipLevel;
        int channels = hasAlpha ? 4 : 3;
        for (int f = 0; f < frames; f++)
        {
            for (int p = 0; p < pixelWidth * pixelHeight; p++)
            {
                writer.Write((byte)(p % 256));                 // R
                writer.Write((byte)((p * 3) % 256));           // G
                writer.Write((byte)((p * 7) % 256));           // B
                if (hasAlpha) writer.Write((byte)(p % 200));   // A
            }
        }

        stream.Position = 0;
        return stream;
    }
}
