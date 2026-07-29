using System.Text.Json;
using System.Text.Json.Serialization;
using GameStudio.Core.Formats;
using GameStudio.Core.Imaging;

namespace GameStudio.Core.Modernize;

public sealed record ModernizeSettings
{
    public UpscaleSettings Upscale { get; init; } = new();
    public PbrOptions Pbr { get; init; } = new();

    /// <summary>Generate normal/roughness/AO alongside the upscaled base colour.</summary>
    public bool GeneratePbr { get; init; } = true;

    /// <summary>Also emit the derived height map, useful for parallax or displacement setups.</summary>
    public bool EmitHeightMap { get; init; }

    /// <summary>Write every frame of an animated texture rather than only the first.</summary>
    public bool AllFrames { get; init; }

    /// <summary>Skip an asset whose outputs already exist and are newer than the source.</summary>
    public bool SkipUpToDate { get; init; } = true;
}

public sealed record ModernizedTexture
{
    public required string SourcePath { get; init; }
    public required string MaterialName { get; init; }
    public required int SourceWidth { get; init; }
    public required int SourceHeight { get; init; }
    public required int OutputWidth { get; init; }
    public required int OutputHeight { get; init; }
    public required bool HasAlpha { get; init; }
    public required int FrameCount { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required UpscaleBackend Backend { get; init; }

    /// <summary>Map role ("BaseColor", "Normal", …) to the written file, relative to the output root.</summary>
    public required Dictionary<string, string> Maps { get; init; }

    public string? Note { get; init; }
}

public sealed record ModernizeReport
{
    public required string OutputDirectory { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required List<ModernizedTexture> Textures { get; init; }
    public required List<string> Skipped { get; init; }
    public required List<string> Failures { get; init; }
}

public readonly record struct ModernizeProgress(int Completed, int Total, string CurrentAsset)
{
    public double Fraction => Total == 0 ? 1d : (double)Completed / Total;
}

/// <summary>
/// Turns legacy Serious Engine textures into a modern PBR material set: an upscaled base colour
/// plus derived normal, roughness and ambient-occlusion maps, written as PNG with a JSON
/// manifest that the Unreal exporter consumes.
/// </summary>
public sealed class ModernizePipeline
{
    private readonly ModernizeSettings _settings;

    public ModernizePipeline(ModernizeSettings? settings = null) => _settings = settings ?? new ModernizeSettings();

    /// <summary>Modernizes every <c>.tex</c> under <paramref name="sourceDirectory"/>.</summary>
    public ModernizeReport RunOnDirectory(string sourceDirectory, string outputDirectory,
        IProgress<ModernizeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = Directory.EnumerateFiles(sourceDirectory, "*.tex", SearchOption.AllDirectories).ToList();
        return Run(files.Select(f => (f, Path.GetRelativePath(sourceDirectory, f))), outputDirectory, progress, cancellationToken);
    }

    /// <summary>Modernizes every texture inside a <c>.gro</c> archive without unpacking it first.</summary>
    public ModernizeReport RunOnArchive(string archivePath, string outputDirectory,
        IProgress<ModernizeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var archive = GroArchive.Open(archivePath);
        var textures = archive.EntriesOfKind(AssetKind.Texture).ToList();

        Directory.CreateDirectory(outputDirectory);
        var results = new List<ModernizedTexture>();
        var skipped = new List<string>();
        var failures = new List<string>();
        int completed = 0;

        foreach (var entry in textures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ModernizeProgress(completed, textures.Count, entry.Path));

            try
            {
                using var stream = archive.ReadEntry(entry.Path);
                var texture = TexFile.Load(stream);
                if (texture.IsEffectTexture) skipped.Add($"{entry.Path} (effect texture, generated at runtime)");
                else results.Add(Convert(texture, entry.Path, entry.Path, outputDirectory, cancellationToken));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
            {
                failures.Add($"{entry.Path}: {ex.Message}");
            }

            completed++;
        }

        progress?.Report(new ModernizeProgress(completed, textures.Count, "done"));
        return WriteManifest(outputDirectory, results, skipped, failures);
    }

    private ModernizeReport Run(IEnumerable<(string FullPath, string RelativePath)> sources, string outputDirectory,
        IProgress<ModernizeProgress>? progress, CancellationToken cancellationToken)
    {
        var list = sources.ToList();
        Directory.CreateDirectory(outputDirectory);

        var results = new List<ModernizedTexture>();
        var skipped = new List<string>();
        var failures = new List<string>();
        int completed = 0;

        foreach (var (fullPath, relativePath) in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ModernizeProgress(completed, list.Count, relativePath));

            try
            {
                if (_settings.SkipUpToDate && IsUpToDate(fullPath, relativePath, outputDirectory))
                {
                    skipped.Add(relativePath);
                }
                else
                {
                    var texture = TexFile.Load(fullPath);
                    if (texture.IsEffectTexture) skipped.Add($"{relativePath} (effect texture, generated at runtime)");
                    else results.Add(Convert(texture, fullPath, relativePath, outputDirectory, cancellationToken));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
            {
                failures.Add($"{relativePath}: {ex.Message}");
            }

            completed++;
        }

        progress?.Report(new ModernizeProgress(completed, list.Count, "done"));
        return WriteManifest(outputDirectory, results, skipped, failures);
    }

    private ModernizedTexture Convert(TexFile texture, string sourcePath, string relativePath,
        string outputDirectory, CancellationToken cancellationToken)
    {
        if (texture.Frames.Count == 0)
            throw new InvalidDataException("Texture contains no frame data.");

        string materialName = SanitizeName(Path.GetFileNameWithoutExtension(relativePath));
        string relativeDirectory = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty;
        string targetDirectory = Path.Combine(outputDirectory, relativeDirectory);
        Directory.CreateDirectory(targetDirectory);

        var maps = new Dictionary<string, string>();
        int frameCount = _settings.AllFrames ? texture.Frames.Count : 1;
        string? note = null;
        UpscaleBackend backend = UpscaleBackend.BuiltIn;
        int outputWidth = 0, outputHeight = 0;

        for (int frame = 0; frame < frameCount; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string suffix = frameCount > 1 ? $"_{frame:D3}" : string.Empty;
            var upscaled = Upscaler.Upscale(texture.Frames[frame], _settings.Upscale, cancellationToken);
            backend = upscaled.BackendUsed;
            note ??= upscaled.Note;
            outputWidth = upscaled.Image.Width;
            outputHeight = upscaled.Image.Height;

            Emit(maps, targetDirectory, relativeDirectory, $"{materialName}{suffix}_BaseColor", "BaseColor" + suffix, upscaled.Image, dropAlpha: !texture.HasAlpha);

            if (_settings.GeneratePbr)
            {
                var pbr = PbrGenerator.Generate(upscaled.Image, _settings.Pbr);
                Emit(maps, targetDirectory, relativeDirectory, $"{materialName}{suffix}_Normal", "Normal" + suffix, pbr.Normal, dropAlpha: true);
                Emit(maps, targetDirectory, relativeDirectory, $"{materialName}{suffix}_Roughness", "Roughness" + suffix, pbr.Roughness, dropAlpha: true);
                Emit(maps, targetDirectory, relativeDirectory, $"{materialName}{suffix}_AO", "AmbientOcclusion" + suffix, pbr.AmbientOcclusion, dropAlpha: true);
                if (pbr.Metallic is not null)
                    Emit(maps, targetDirectory, relativeDirectory, $"{materialName}{suffix}_Metallic", "Metallic" + suffix, pbr.Metallic, dropAlpha: true);
                if (_settings.EmitHeightMap)
                    Emit(maps, targetDirectory, relativeDirectory, $"{materialName}{suffix}_Height", "Height" + suffix, pbr.Height, dropAlpha: true);
            }
        }

        return new ModernizedTexture
        {
            SourcePath = relativePath.Replace('\\', '/'),
            MaterialName = materialName,
            SourceWidth = texture.PixelWidth,
            SourceHeight = texture.PixelHeight,
            OutputWidth = outputWidth,
            OutputHeight = outputHeight,
            HasAlpha = texture.HasAlpha,
            FrameCount = texture.Frames.Count,
            Backend = backend,
            Maps = maps,
            Note = note,
        };
    }

    private static void Emit(Dictionary<string, string> maps, string targetDirectory, string relativeDirectory,
        string fileName, string role, RgbaImage image, bool dropAlpha)
    {
        string file = fileName + ".png";
        PngWriter.Save(image, Path.Combine(targetDirectory, file), dropAlpha);
        maps[role] = string.IsNullOrEmpty(relativeDirectory) ? file : $"{relativeDirectory}/{file}";
    }

    private bool IsUpToDate(string sourcePath, string relativePath, string outputDirectory)
    {
        string materialName = SanitizeName(Path.GetFileNameWithoutExtension(relativePath));
        string relativeDirectory = Path.GetDirectoryName(relativePath) ?? string.Empty;
        string baseColor = Path.Combine(outputDirectory, relativeDirectory, materialName + "_BaseColor.png");
        return File.Exists(baseColor) && File.GetLastWriteTimeUtc(baseColor) >= File.GetLastWriteTimeUtc(sourcePath);
    }

    private static ModernizeReport WriteManifest(string outputDirectory, List<ModernizedTexture> textures,
        List<string> skipped, List<string> failures)
    {
        var report = new ModernizeReport
        {
            OutputDirectory = outputDirectory,
            GeneratedAt = DateTimeOffset.UtcNow,
            Textures = textures,
            Skipped = skipped,
            Failures = failures,
        };
        File.WriteAllText(Path.Combine(outputDirectory, "materials.json"), JsonSerializer.Serialize(report, GameStudioJson.Options));
        return report;
    }

    /// <summary>Reduces an engine asset name to the identifier characters Unreal accepts.</summary>
    public static string SanitizeName(string name)
    {
        Span<char> buffer = name.Length <= 128 ? stackalloc char[name.Length] : new char[name.Length];
        int length = 0;
        foreach (char c in name)
            buffer[length++] = char.IsLetterOrDigit(c) || c == '_' ? c : '_';

        string result = new(buffer[..length]);
        return result.Length == 0 || char.IsDigit(result[0]) ? "T_" + result : result;
    }
}
