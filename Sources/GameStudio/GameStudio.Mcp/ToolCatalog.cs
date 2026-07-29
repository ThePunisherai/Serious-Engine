using System.Text.Json;
using System.Text.Json.Nodes;
using GameStudio.Core;
using GameStudio.Core.Formats;
using GameStudio.Core.Imaging;
using GameStudio.Core.Modernize;
using GameStudio.Core.Unreal;

namespace GameStudio.Mcp;

/// <summary>
/// The tools this server exposes, and their dispatch. Every path argument is resolved through
/// <see cref="ResolvePath"/>, which confines access to the configured workspace root.
/// </summary>
public sealed class ToolCatalog
{
    private readonly string? _root;

    /// <param name="root">
    /// Directory every path argument must stay inside. Null allows the whole filesystem, which is
    /// only appropriate when the client itself is already trusted and sandboxed.
    /// </param>
    public ToolCatalog(string? root) => _root = root is null ? null : Path.GetFullPath(root);

    public JsonArray Describe() =>
    [
        Tool("engine_overview",
            "Summarize a Serious Engine installation: resource archives, asset counts by kind, and total size.",
            new JsonObject
            {
                ["engineRoot"] = Property("string", "Directory containing the engine's .gro archives (the folder with SE1_10.gro)."),
            },
            required: ["engineRoot"]),

        Tool("list_archive",
            "List the entries of a .gro resource archive, optionally filtered by asset kind or a path substring.",
            new JsonObject
            {
                ["archive"] = Property("string", "Path to the .gro file."),
                ["kind"] = Property("string", "Optional asset kind filter, e.g. Texture, Model, World, Sound."),
                ["contains"] = Property("string", "Optional case-insensitive substring the entry path must contain."),
                ["limit"] = Property("integer", "Maximum entries to return. Defaults to 100."),
            },
            required: ["archive"]),

        Tool("texture_info",
            "Decode a .tex header and report its format version, dimensions, alpha and frame count.",
            new JsonObject
            {
                ["texture"] = Property("string", "Path to a .tex file, or 'archive.gro::entry/path.tex' to read one from an archive."),
            },
            required: ["texture"]),

        Tool("export_texture",
            "Decode a .tex file and write one frame out as a PNG.",
            new JsonObject
            {
                ["texture"] = Property("string", "Path to a .tex file, or 'archive.gro::entry/path.tex'."),
                ["output"] = Property("string", "Destination .png path."),
                ["frame"] = Property("integer", "Frame index for animated textures. Defaults to 0."),
            },
            required: ["texture", "output"]),

        Tool("modernize_textures",
            "Upscale legacy textures and derive PBR maps (normal, roughness, ambient occlusion), writing PNGs plus a materials.json manifest.",
            new JsonObject
            {
                ["source"] = Property("string", "A directory of .tex files, or a .gro archive to read them from directly."),
                ["output"] = Property("string", "Directory to write the modernized material set into."),
                ["scale"] = Property("integer", "Upscale factor, 1-8. Defaults to 4."),
                ["filter"] = Property("string", "Resampling filter: Nearest, Bilinear, Bicubic or Lanczos3. Defaults to Lanczos3."),
                ["generatePbr"] = Property("boolean", "Derive normal/roughness/AO maps. Defaults to true."),
                ["upscalerPath"] = Property("string", "Optional external neural upscaler executable, e.g. realesrgan-ncnn-vulkan. Falls back to built-in resampling when absent."),
            },
            required: ["source", "output"]),

        Tool("export_unreal",
            "Turn a modernized material set into an Unreal Engine 5 import package: a manifest plus a Python script that imports the textures and builds wired materials.",
            new JsonObject
            {
                ["modernizedDirectory"] = Property("string", "Directory containing materials.json from modernize_textures."),
                ["output"] = Property("string", "Directory to write import_to_unreal.py and unreal_import.json into."),
                ["contentRoot"] = Property("string", "Unreal content path for the imported assets. Defaults to /Game/SeriousEngine."),
            },
            required: ["modernizedDirectory", "output"]),
    ];

    public async Task<JsonNode> CallAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        try
        {
            string text = name switch
            {
                "engine_overview" => EngineOverview(arguments),
                "list_archive" => ListArchive(arguments),
                "texture_info" => TextureInfo(arguments),
                "export_texture" => ExportTexture(arguments),
                "modernize_textures" => await Task.Run(() => ModernizeTextures(arguments, cancellationToken), cancellationToken),
                "export_unreal" => ExportUnreal(arguments),
                _ => throw new ArgumentException($"Unknown tool '{name}'."),
            };
            return Content(text, isError: false);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Content($"{name} failed: {ex.Message}", isError: true);
        }
    }

    private string EngineOverview(JsonObject arguments)
    {
        string root = ResolvePath(RequireString(arguments, "engineRoot"));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"'{root}' is not a directory.");

        var archives = new JsonArray();
        long totalBytes = 0;

        foreach (string file in Directory.EnumerateFiles(root, "*.gro", SearchOption.TopDirectoryOnly).Order())
        {
            using var archive = GroArchive.Open(file);
            var byKind = archive.Entries.GroupBy(e => e.Kind)
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key.ToString(), g => g.Count());

            totalBytes += archive.TotalUncompressedBytes;
            archives.Add(new JsonObject
            {
                ["archive"] = Path.GetFileName(file),
                ["entries"] = archive.Entries.Count,
                ["uncompressedBytes"] = archive.TotalUncompressedBytes,
                ["byKind"] = JsonSerializer.SerializeToNode(byKind),
            });
        }

        return new JsonObject
        {
            ["engineRoot"] = root,
            ["archiveCount"] = archives.Count,
            ["totalUncompressedBytes"] = totalBytes,
            ["archives"] = archives,
        }.ToJsonString(GameStudioJson.Options);
    }

    private string ListArchive(JsonObject arguments)
    {
        string path = ResolvePath(RequireString(arguments, "archive"));
        string? kindFilter = OptionalString(arguments, "kind");
        string? contains = OptionalString(arguments, "contains");
        int limit = Math.Clamp(OptionalInt(arguments, "limit") ?? 100, 1, 5000);

        using var archive = GroArchive.Open(path);
        IEnumerable<GroEntry> entries = archive.Entries;

        if (!string.IsNullOrEmpty(kindFilter))
        {
            if (!Enum.TryParse<AssetKind>(kindFilter, ignoreCase: true, out var kind))
                throw new ArgumentException($"Unknown asset kind '{kindFilter}'. Valid values: {string.Join(", ", Enum.GetNames<AssetKind>())}.");
            entries = entries.Where(e => e.Kind == kind);
        }

        if (!string.IsNullOrEmpty(contains))
            entries = entries.Where(e => e.Path.Contains(contains, StringComparison.OrdinalIgnoreCase));

        var matched = entries.ToList();
        var listed = new JsonArray();
        foreach (var entry in matched.Take(limit))
            listed.Add(new JsonObject
            {
                ["path"] = entry.Path,
                ["kind"] = entry.Kind.ToString(),
                ["bytes"] = entry.Length,
            });

        return new JsonObject
        {
            ["archive"] = path,
            ["totalEntries"] = archive.Entries.Count,
            ["matched"] = matched.Count,
            ["returned"] = listed.Count,
            ["entries"] = listed,
        }.ToJsonString(GameStudioJson.Options);
    }

    private string TextureInfo(JsonObject arguments)
    {
        var (texture, source) = LoadTexture(RequireString(arguments, "texture"));
        return new JsonObject
        {
            ["source"] = source,
            ["formatVersion"] = texture.Version,
            ["width"] = texture.PixelWidth,
            ["height"] = texture.PixelHeight,
            ["hasAlpha"] = texture.HasAlpha,
            ["declaredFrames"] = texture.DeclaredFrameCount,
            ["decodedFrames"] = texture.Frames.Count,
            ["isEffectTexture"] = texture.IsEffectTexture,
            ["flags"] = texture.Flags.ToString(),
            ["note"] = texture.IsEffectTexture
                ? "Effect texture: pixels are simulated at runtime, so no frames are stored on disk."
                : null,
        }.ToJsonString(GameStudioJson.Options);
    }

    private string ExportTexture(JsonObject arguments)
    {
        var (texture, source) = LoadTexture(RequireString(arguments, "texture"));
        string output = ResolvePath(RequireString(arguments, "output"));
        int frame = OptionalInt(arguments, "frame") ?? 0;

        if (texture.Frames.Count == 0)
            throw new InvalidDataException("This texture stores no pixel data (effect textures are generated at runtime).");
        if (frame < 0 || frame >= texture.Frames.Count)
            throw new ArgumentException($"Frame {frame} is out of range; the texture has {texture.Frames.Count}.");

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        PngWriter.Save(texture.Frames[frame], output);

        return new JsonObject
        {
            ["source"] = source,
            ["output"] = output,
            ["frame"] = frame,
            ["width"] = texture.Frames[frame].Width,
            ["height"] = texture.Frames[frame].Height,
        }.ToJsonString(GameStudioJson.Options);
    }

    private string ModernizeTextures(JsonObject arguments, CancellationToken cancellationToken)
    {
        string source = ResolvePath(RequireString(arguments, "source"));
        string output = ResolvePath(RequireString(arguments, "output"));
        int scale = Math.Clamp(OptionalInt(arguments, "scale") ?? 4, 1, 8);
        string filterName = OptionalString(arguments, "filter") ?? nameof(ResampleFilter.Lanczos3);
        string? upscalerPath = OptionalString(arguments, "upscalerPath");

        if (!Enum.TryParse<ResampleFilter>(filterName, ignoreCase: true, out var filter))
            throw new ArgumentException($"Unknown filter '{filterName}'. Valid values: {string.Join(", ", Enum.GetNames<ResampleFilter>())}.");

        var settings = new ModernizeSettings
        {
            GeneratePbr = OptionalBool(arguments, "generatePbr") ?? true,
            Upscale = new UpscaleSettings
            {
                Factor = scale,
                Filter = filter,
                Backend = string.IsNullOrEmpty(upscalerPath) ? UpscaleBackend.BuiltIn : UpscaleBackend.External,
                ExecutablePath = string.IsNullOrEmpty(upscalerPath) ? null : ResolvePath(upscalerPath),
            },
        };

        var pipeline = new ModernizePipeline(settings);
        var report = Directory.Exists(source)
            ? pipeline.RunOnDirectory(source, output, cancellationToken: cancellationToken)
            : pipeline.RunOnArchive(source, output, cancellationToken: cancellationToken);

        return new JsonObject
        {
            ["source"] = source,
            ["output"] = output,
            ["converted"] = report.Textures.Count,
            ["skipped"] = report.Skipped.Count,
            ["failed"] = report.Failures.Count,
            ["manifest"] = Path.Combine(output, "materials.json"),
            ["firstFailures"] = new JsonArray(report.Failures.Take(10).Select(f => (JsonNode)f!).ToArray()),
        }.ToJsonString(GameStudioJson.Options);
    }

    private string ExportUnreal(JsonObject arguments)
    {
        string modernized = ResolvePath(RequireString(arguments, "modernizedDirectory"));
        string output = ResolvePath(RequireString(arguments, "output"));
        string contentRoot = OptionalString(arguments, "contentRoot") ?? "/Game/SeriousEngine";

        string manifestPath = Path.Combine(modernized, "materials.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"No materials.json in '{modernized}'. Run modernize_textures first.", manifestPath);

        var report = JsonSerializer.Deserialize<ModernizeReport>(File.ReadAllText(manifestPath), GameStudioJson.Options)
            ?? throw new InvalidDataException("materials.json could not be read.");

        var result = UnrealExporter.Export(report, output, new UnrealExportSettings { ContentRoot = contentRoot });

        return new JsonObject
        {
            ["script"] = result.ScriptPath,
            ["manifest"] = result.ManifestPath,
            ["materials"] = result.MaterialCount,
            ["textures"] = result.TextureCount,
            ["nextStep"] = $"In the Unreal Editor, run:  py \"{result.ScriptPath}\"",
        }.ToJsonString(GameStudioJson.Options);
    }

    /// <summary>
    /// Loads a texture from a plain path, or from inside an archive using
    /// <c>archive.gro::entry/path.tex</c>.
    /// </summary>
    private (TexFile Texture, string Source) LoadTexture(string specifier)
    {
        int separator = specifier.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0)
        {
            string path = ResolvePath(specifier);
            return (TexFile.Load(path), path);
        }

        string archivePath = ResolvePath(specifier[..separator]);
        string entry = specifier[(separator + 2)..];

        using var archive = GroArchive.Open(archivePath);
        using var stream = archive.ReadEntry(entry);
        return (TexFile.Load(stream), $"{archivePath}::{entry}");
    }

    /// <summary>Resolves a path and refuses anything outside the configured workspace root.</summary>
    private string ResolvePath(string path)
    {
        string full = Path.GetFullPath(path);
        if (_root is null) return full;

        if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"'{path}' is outside the workspace root '{_root}'.");
        return full;
    }

    private static string RequireString(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"Missing required argument '{name}'.");

    private static string? OptionalString(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>() is { Length: > 0 } value ? value : null;

    private static int? OptionalInt(JsonObject arguments, string name) =>
        arguments[name] is { } node && node.GetValueKind() == JsonValueKind.Number ? node.GetValue<int>() : null;

    private static bool? OptionalBool(JsonObject arguments, string name) =>
        arguments[name] is { } node && node.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? node.GetValue<bool>()
            : null;

    private static JsonObject Property(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };

    private static JsonObject Tool(string name, string description, JsonObject properties, string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r!).ToArray()),
        },
    };

    private static JsonNode Content(string text, bool isError) => new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };
}
