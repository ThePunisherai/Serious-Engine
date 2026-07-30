using System.Numerics;
using GameStudio.Core.Formats;
using GameStudio.Core.Imaging;

namespace GameStudio.Core.Modernize;

public sealed record UpgradeSettings
{
    /// <summary>Largest factor to sharpen by. Clamped per texture to what its headroom allows.</summary>
    public int MaxScale { get; init; } = 4;

    public ResampleFilter Filter { get; init; } = ResampleFilter.Lanczos3;

    /// <summary>
    /// Pixel ceiling, matching <c>MAX_TEXTURE_DIMENSION</c> in the engine. Writing past it produces
    /// a file the engine refuses to load.
    /// </summary>
    public int MaxDimension { get; init; } = 4096;
}

/// <summary>What happened to one texture, including the ones deliberately left alone.</summary>
public sealed record UpgradedTexture(
    string Name,
    int OriginalWidth,
    int OriginalHeight,
    int UpgradedWidth,
    int UpgradedHeight,
    int Factor,
    int FirstMipLevel,
    string? SkipReason = null)
{
    public bool Upgraded => SkipReason is null;
}

public sealed record UpgradeReport(
    string OutputDirectory,
    IReadOnlyList<UpgradedTexture> Textures)
{
    public int UpgradedCount => Textures.Count(t => t.Upgraded);
    public int SkippedCount => Textures.Count(t => !t.Upgraded);
}

/// <summary>
/// Rewrites engine textures at higher resolution, in the engine's own format, so an existing game
/// simply loads sharper artwork with no engine change.
/// <para>
/// The trick is that a <c>.tex</c> stores two different sizes. <c>mexWidth</c> is the world-space
/// size — how large the texture appears on a surface — and the stored pixels sit
/// <c>firstMipLevel</c> powers of two below it. Most of the shipped artwork has room there: of the
/// 724 textures in <c>SE1_10.gro</c>, 560 store fewer pixels than their mex size allows, some by a
/// factor of 64. Lowering <c>firstMipLevel</c> and writing denser pixels therefore sharpens a
/// texture while leaving its world scale untouched.
/// </para>
/// <para>
/// Raising <c>mexWidth</c> instead would be the obvious-looking move and is exactly wrong: it
/// rescales the texture on every surface that uses it, so a wall would suddenly show four tiles
/// where it used to show one.
/// </para>
/// </summary>
public static class TextureUpgrade
{
    /// <summary>
    /// How much sharper this texture is allowed to get: its mip headroom, capped by the requested
    /// scale and by the engine's dimension limit. Always a power of two; 1 means leave it alone.
    /// </summary>
    public static int FactorFor(TexFile texture, UpgradeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(settings);

        int factor = 1;
        while (factor * 2 <= settings.MaxScale
            && texture.FirstMipLevel >= BitOperations.Log2((uint)(factor * 2))
            && texture.PixelWidth * factor * 2 <= settings.MaxDimension
            && texture.PixelHeight * factor * 2 <= settings.MaxDimension)
        {
            factor *= 2;
        }
        return factor;
    }

    /// <summary>
    /// Upgrades one texture and writes it to <paramref name="outputPath"/>. A texture with nothing
    /// to gain is reported with a <see cref="UpgradedTexture.SkipReason"/> and no file is written.
    /// </summary>
    public static UpgradedTexture Upgrade(TexFile texture, string name, string outputPath,
        UpgradeSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(texture);
        settings ??= new UpgradeSettings();

        if (texture.IsEffectTexture)
            return Skipped(texture, name, "effect texture — its frames are simulated, not stored");

        if (texture.Frames.Count == 0)
            return Skipped(texture, name, "no stored frames");

        int factor = FactorFor(texture, settings);
        if (factor <= 1)
            return Skipped(texture, name, texture.FirstMipLevel == 0
                ? "already stored at its full world-space size"
                : "the engine's dimension limit leaves no room");

        int shift = BitOperations.Log2((uint)factor);
        int newFirstMip = texture.FirstMipLevel - shift;

        var frames = new List<RgbaImage>(texture.Frames.Count);
        foreach (RgbaImage frame in texture.Frames)
            frames.Add(Resampler.Resize(frame, frame.Width * factor, frame.Height * factor, settings.Filter));

        // Mirror of what the engine does when it skips mips on load: dropping levels lowers the
        // fine-mip count by the same amount, so adding levels raises it.
        int fineMipLevels = Math.Max(1, texture.FineMipLevels + shift);

        TexWriter.Write(outputPath, frames, texture.Flags, texture.MexWidth, texture.MexHeight,
            fineMipLevels, newFirstMip);

        return new UpgradedTexture(name, texture.PixelWidth, texture.PixelHeight,
            texture.PixelWidth * factor, texture.PixelHeight * factor, factor, newFirstMip);
    }

    /// <summary>Upgrades every texture in a <c>.gro</c>, mirroring the archive's layout on disk.</summary>
    public static UpgradeReport RunOnArchive(string archivePath, string outputDirectory,
        UpgradeSettings? settings = null, CancellationToken cancellationToken = default)
    {
        using var archive = GroArchive.Open(archivePath);
        Directory.CreateDirectory(outputDirectory);

        var results = new List<UpgradedTexture>();
        foreach (var entry in archive.EntriesOfKind(AssetKind.Texture))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Stream stream = archive.ReadEntry(entry.Path);
            results.Add(UpgradeOne(() => TexFile.Load(stream), entry.Path,
                Path.Combine(outputDirectory, entry.Path), settings));
        }
        return new UpgradeReport(outputDirectory, results);
    }

    /// <summary>Upgrades every <c>.tex</c> under a directory, preserving the relative layout.</summary>
    public static UpgradeReport RunOnDirectory(string sourceDirectory, string outputDirectory,
        UpgradeSettings? settings = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var results = new List<UpgradedTexture>();
        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*.tex", SearchOption.AllDirectories).Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(sourceDirectory, file);
            results.Add(UpgradeOne(() => TexFile.Load(file), relative,
                Path.Combine(outputDirectory, relative), settings));
        }
        return new UpgradeReport(outputDirectory, results);
    }

    /// <summary>
    /// A texture that will not decode is reported rather than thrown, so one damaged file in an
    /// archive of hundreds does not abandon the whole run.
    /// </summary>
    private static UpgradedTexture UpgradeOne(Func<TexFile> load, string name, string outputPath,
        UpgradeSettings? settings)
    {
        try
        {
            return Upgrade(load(), name, outputPath, settings);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return new UpgradedTexture(name, 0, 0, 0, 0, 1, 0, $"could not be read: {ex.Message}");
        }
    }

    private static UpgradedTexture Skipped(TexFile texture, string name, string reason) =>
        new(name, texture.PixelWidth, texture.PixelHeight, texture.PixelWidth, texture.PixelHeight,
            1, texture.FirstMipLevel, reason);
}
