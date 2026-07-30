using GameStudio.Core.Formats;
using GameStudio.Core.Imaging;
using GameStudio.Core.Modernize;

namespace GameStudio.Core.Tests;

public class TexWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameStudioTexTests", Guid.NewGuid().ToString("N"));

    public TexWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static RgbaImage Gradient(int width, int height, byte alpha = 255)
    {
        var image = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                image.SetPixel(x, y, (byte)(x * 7), (byte)(y * 11), (byte)(x + y), alpha);
        return image;
    }

    [Fact]
    public void AnOpaqueTextureSurvivesAWriteAndReadUnchanged()
    {
        string path = Path.Combine(_root, "opaque.tex");
        RgbaImage source = Gradient(16, 8);

        TexWriter.Write(path, [source], TexFlags.None,
            mexWidth: 64, mexHeight: 32, fineMipLevels: 3, firstMipLevel: 2);

        TexFile read = TexFile.Load(path);
        Assert.Equal(4, read.Version);
        Assert.Equal(64, read.MexWidth);
        Assert.Equal(2, read.FirstMipLevel);
        Assert.Equal(16, read.PixelWidth);
        Assert.Equal(8, read.PixelHeight);

        RgbaImage frame = Assert.Single(read.Frames);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(source.GetPixel(x, y), frame.GetPixel(x, y));
    }

    [Fact]
    public void AlphaSurvivesWhenTheFlagIsSet()
    {
        string path = Path.Combine(_root, "alpha.tex");
        RgbaImage source = Gradient(8, 8, alpha: 128);

        TexWriter.Write(path, [source], TexFlags.AlphaChannel,
            mexWidth: 8, mexHeight: 8, fineMipLevels: 1, firstMipLevel: 0);

        RgbaImage frame = Assert.Single(TexFile.Load(path).Frames);
        Assert.Equal(128, frame.GetPixel(3, 4).A);
    }

    [Fact]
    public void WithoutTheAlphaFlagTheChannelIsDroppedAndReadsBackOpaque()
    {
        string path = Path.Combine(_root, "noalpha.tex");

        TexWriter.Write(path, [Gradient(8, 8, alpha: 7)], TexFlags.None,
            mexWidth: 8, mexHeight: 8, fineMipLevels: 1, firstMipLevel: 0);

        RgbaImage frame = Assert.Single(TexFile.Load(path).Frames);
        Assert.Equal(255, frame.GetPixel(3, 4).A);
    }

    [Fact]
    public void EveryFrameOfAnAnimationIsWritten()
    {
        string path = Path.Combine(_root, "anim.tex");

        TexWriter.Write(path, [Gradient(4, 4), Gradient(4, 4), Gradient(4, 4)], TexFlags.None,
            mexWidth: 4, mexHeight: 4, fineMipLevels: 1, firstMipLevel: 0);

        TexFile read = TexFile.Load(path);
        Assert.Equal(3, read.Frames.Count);
        Assert.True(read.IsAnimated);
    }

    [Fact]
    public void AFrameThatDoesNotMatchTheHeaderIsRefused()
    {
        // 32 >> 1 is 16, so a 20-wide frame cannot be what this header describes.
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            TexWriter.Write(Path.Combine(_root, "bad.tex"), [Gradient(20, 16)], TexFlags.None,
                mexWidth: 32, mexHeight: 32, fineMipLevels: 1, firstMipLevel: 1));

        Assert.Contains("20x16", ex.Message);
    }
}

public class TextureUpgradeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameStudioUpgradeTests", Guid.NewGuid().ToString("N"));

    public TextureUpgradeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Builds a texture whose pixels sit <paramref name="firstMipLevel"/> levels below its mex size.</summary>
    private TexFile MakeTexture(int mexSize, int firstMipLevel, int fineMipLevels = 1)
    {
        int pixels = mexSize >> firstMipLevel;
        string path = Path.Combine(_root, $"src_{mexSize}_{firstMipLevel}.tex");
        TexWriter.Write(path, [new RgbaImage(pixels, pixels)], TexFlags.None,
            mexSize, mexSize, fineMipLevels, firstMipLevel);
        return TexFile.Load(path);
    }

    [Fact]
    public void HeadroomIsTheMipDistanceBetweenStoredPixelsAndWorldSize()
    {
        // 512 mex storing 64 pixels is three levels down, so 8x is available but 4x was asked for.
        Assert.Equal(4, TextureUpgrade.FactorFor(MakeTexture(512, 3), new UpgradeSettings { MaxScale = 4 }));
        Assert.Equal(8, TextureUpgrade.FactorFor(MakeTexture(512, 3), new UpgradeSettings { MaxScale = 8 }));

        // One level down allows exactly one doubling, however much is asked for.
        Assert.Equal(2, TextureUpgrade.FactorFor(MakeTexture(512, 1), new UpgradeSettings { MaxScale = 8 }));
    }

    [Fact]
    public void ATextureAlreadyAtItsWorldSizeIsLeftAlone()
    {
        Assert.Equal(1, TextureUpgrade.FactorFor(MakeTexture(256, 0), new UpgradeSettings { MaxScale = 8 }));

        UpgradedTexture result = TextureUpgrade.Upgrade(
            MakeTexture(256, 0), "flat.tex", Path.Combine(_root, "out.tex"));

        Assert.False(result.Upgraded);
        Assert.Contains("full world-space size", result.SkipReason);
        Assert.False(File.Exists(Path.Combine(_root, "out.tex")));
    }

    [Fact]
    public void TheEngineDimensionLimitCapsTheFactor()
    {
        // 8192 mex storing 2048 pixels has 4x of headroom, but 4096 is as far as the engine goes.
        var settings = new UpgradeSettings { MaxScale = 8, MaxDimension = 4096 };
        Assert.Equal(2, TextureUpgrade.FactorFor(MakeTexture(8192, 2), settings));
    }

    [Fact]
    public void UpgradingPreservesWorldScaleAndOnlyRaisesPixelDensity()
    {
        TexFile source = MakeTexture(1024, 3, fineMipLevels: 2);
        string output = Path.Combine(_root, "upgraded.tex");

        UpgradedTexture result = TextureUpgrade.Upgrade(source, "wall.tex", output,
            new UpgradeSettings { MaxScale = 4 });

        Assert.True(result.Upgraded);
        Assert.Equal(4, result.Factor);
        Assert.Equal(128, result.OriginalWidth);
        Assert.Equal(512, result.UpgradedWidth);

        TexFile written = TexFile.Load(output);

        // The whole point: mex is untouched, so the texture covers exactly the same world area.
        Assert.Equal(source.MexWidth, written.MexWidth);
        Assert.Equal(source.MexHeight, written.MexHeight);

        // Two mip levels closer to the mex size, and four times the pixels.
        Assert.Equal(source.FirstMipLevel - 2, written.FirstMipLevel);
        Assert.Equal(512, written.PixelWidth);
        Assert.Equal(512, written.PixelHeight);

        // Fine mip count moves with the level count, mirroring what the engine does when skipping.
        Assert.Equal(source.FineMipLevels + 2, written.FineMipLevels);
    }

    [Fact]
    public void ADirectoryRunMirrorsItsLayout()
    {
        string source = Path.Combine(_root, "in");
        string nested = Path.Combine(source, "Models", "Egypt");
        Directory.CreateDirectory(nested);

        TexWriter.Write(Path.Combine(nested, "wall.tex"), [new RgbaImage(32, 32)], TexFlags.None,
            mexWidth: 256, mexHeight: 256, fineMipLevels: 1, firstMipLevel: 3);

        string output = Path.Combine(_root, "out");
        UpgradeReport report = TextureUpgrade.RunOnDirectory(source, output);

        Assert.Equal(1, report.UpgradedCount);
        Assert.True(File.Exists(Path.Combine(output, "Models", "Egypt", "wall.tex")));
    }

    [Fact]
    public void AnUnreadableFileIsReportedRatherThanAbandoningTheRun()
    {
        string source = Path.Combine(_root, "mixed");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "broken.tex"), "this is not a texture");
        TexWriter.Write(Path.Combine(source, "good.tex"), [new RgbaImage(16, 16)], TexFlags.None,
            mexWidth: 64, mexHeight: 64, fineMipLevels: 1, firstMipLevel: 2);

        UpgradeReport report = TextureUpgrade.RunOnDirectory(source, Path.Combine(_root, "out2"));

        Assert.Equal(2, report.Textures.Count);
        Assert.Equal(1, report.UpgradedCount);
        Assert.Contains(report.Textures, t => t.SkipReason?.StartsWith("could not be read") == true);
    }
}
