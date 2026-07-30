using System.Text.Json;
using System.Text.Json.Nodes;
using GameStudio.Core;
using GameStudio.Core.Formats;
using GameStudio.Core.Imaging;
using GameStudio.Core.Modernize;
using GameStudio.Core.Unreal;
using GameStudio.Core.Worlds;

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

        Tool("create_map",
            "Start a new map definition and write it to disk. Rooms, lights and props are added afterwards with the map_add_* tools.",
            new JsonObject
            {
                ["map"] = Property("string", "Path of the .map.json file to create."),
                ["name"] = Property("string", "Name of the level."),
                ["description"] = Property("string", "Optional description of the level."),
                ["playerStart"] = Property("array", "Player start position as [x, y, z] in centimetres. Defaults to [0, 0, 100]."),
                ["daylight"] = Property("boolean", "Spawn a sun, sky light and sky atmosphere. Defaults to true."),
                ["hardwareRayTracing"] = Property("boolean", "Trace Lumen against real triangles rather than distance fields. Defaults to true."),
                ["lumenQuality"] = Property("number", "Lumen final-gather quality multiplier. 1 is the Unreal default, 2 roughly doubles the ray budget."),
                ["overwrite"] = Property("boolean", "Replace an existing file. Defaults to false."),
            },
            required: ["map", "name"]),

        Tool("map_add_room",
            "Add a box-shaped room to a map. Hollow rooms become a shell of six slabs; solid ones a single filled block.",
            new JsonObject
            {
                ["map"] = Property("string", "Path to the .map.json file."),
                ["name"] = Property("string", "Name of the room."),
                ["center"] = Property("array", "Centre as [x, y, z] in centimetres."),
                ["size"] = Property("array", "Outer dimensions as [x, y, z] in centimetres."),
                ["hollow"] = Property("boolean", "Build a room you can stand in rather than a solid block. Defaults to true."),
                ["wallThickness"] = Property("number", "Shell thickness for hollow rooms. Defaults to 20."),
                ["material"] = Property("string", "Material asset name, resolved against the export content root."),
                ["openFaces"] = Property("array", "Faces to leave open so rooms connect: Floor, Ceiling, North, South, East, West."),
            },
            required: ["map", "name"]),

        Tool("map_add_light",
            "Add a light to a map. Dynamic lights participate in Lumen's ray-traced bounces.",
            new JsonObject
            {
                ["map"] = Property("string", "Path to the .map.json file."),
                ["name"] = Property("string", "Name of the light."),
                ["kind"] = Property("string", "Point, Spot, Directional or Rect. Defaults to Point."),
                ["position"] = Property("array", "Position as [x, y, z] in centimetres."),
                ["rotation"] = Property("array", "Rotation as [pitch, yaw, roll] in degrees."),
                ["intensity"] = Property("number", "Candelas for point/spot lights, lux for directional ones."),
                ["color"] = Property("array", "Linear colour as [r, g, b], each 0-1. Defaults to white."),
                ["radius"] = Property("number", "Attenuation radius in centimetres. Defaults to 1000."),
                ["coneAngle"] = Property("number", "Outer cone angle in degrees, spot lights only."),
                ["castShadows"] = Property("boolean", "Defaults to true."),
                ["dynamic"] = Property("boolean", "Movable rather than static, so Lumen lights it dynamically. Defaults to true."),
            },
            required: ["map", "name"]),

        Tool("map_add_prop",
            "Place a static mesh in a map, either an Unreal built-in shape or an imported asset.",
            new JsonObject
            {
                ["map"] = Property("string", "Path to the .map.json file."),
                ["name"] = Property("string", "Name of the placed actor."),
                ["mesh"] = Property("string", "Static mesh asset path, e.g. /Engine/BasicShapes/Cylinder."),
                ["position"] = Property("array", "Position as [x, y, z] in centimetres."),
                ["rotation"] = Property("array", "Rotation as [pitch, yaw, roll] in degrees."),
                ["scale"] = Property("array", "Scale as [x, y, z]. Defaults to [1, 1, 1]."),
                ["material"] = Property("string", "Material asset name to override the mesh's own."),
            },
            required: ["map", "name", "mesh"]),

        Tool("map_describe",
            "Summarize a map: its rooms, lights, props, bounds and rendering settings.",
            new JsonObject
            {
                ["map"] = Property("string", "Path to the .map.json file."),
            },
            required: ["map"]),

        Tool("export_map_unreal",
            "Turn a map definition into an Unreal Engine 5 level package: a manifest plus a Python script that builds the geometry, lights and a post-process volume configured for Lumen.",
            new JsonObject
            {
                ["map"] = Property("string", "Path to the .map.json file."),
                ["output"] = Property("string", "Directory to write build_level.py and level.json into."),
                ["contentRoot"] = Property("string", "Unreal content path materials resolve against. Defaults to /Game/SeriousEngine."),
            },
            required: ["map", "output"]),
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
                "create_map" => CreateMap(arguments),
                "map_add_room" => MapAddRoom(arguments),
                "map_add_light" => MapAddLight(arguments),
                "map_add_prop" => MapAddProp(arguments),
                "map_describe" => MapDescribe(arguments),
                "export_map_unreal" => ExportMapUnreal(arguments),
                _ => throw new ArgumentException($"Unknown tool '{name}'."),
            };
            return Content(text, isError: false);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException
                                        or UnauthorizedAccessException or JsonException)
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

    private string CreateMap(JsonObject arguments)
    {
        string path = ResolvePath(RequireString(arguments, "map"));
        if (File.Exists(path) && OptionalBool(arguments, "overwrite") != true)
            throw new ArgumentException($"'{path}' already exists. Pass overwrite to replace it.");

        var environment = new MapEnvironment
        {
            Daylight = OptionalBool(arguments, "daylight") ?? true,
            HardwareRayTracing = OptionalBool(arguments, "hardwareRayTracing") ?? true,
            LumenQuality = OptionalDouble(arguments, "lumenQuality") ?? 2.0,
        };

        var map = new MapDefinition
        {
            Name = RequireString(arguments, "name"),
            Description = OptionalString(arguments, "description"),
            PlayerStart = ReadVec3(arguments, "playerStart") ?? new Vec3(0, 0, 100),
            Environment = environment,
        };
        map.Save(path);

        return $"Created map '{map.Name}' at {path}. "
             + $"Lumen GI {(environment.LumenGlobalIllumination ? "on" : "off")}, "
             + $"hardware ray tracing {(environment.HardwareRayTracing ? "on" : "off")}, "
             + $"quality {environment.LumenQuality:0.##}.";
    }

    private string MapAddRoom(JsonObject arguments)
    {
        string path = ResolvePath(RequireString(arguments, "map"));
        MapDefinition map = MapDefinition.Load(path);

        var room = new MapRoom
        {
            Name = RequireString(arguments, "name"),
            Center = ReadVec3(arguments, "center") ?? Vec3.Zero,
            Size = ReadVec3(arguments, "size") ?? new Vec3(800, 800, 400),
            Hollow = OptionalBool(arguments, "hollow") ?? true,
            WallThickness = OptionalDouble(arguments, "wallThickness") ?? 20,
            Material = OptionalString(arguments, "material"),
            OpenFaces = ReadStrings(arguments, "openFaces"),
        };

        map = map.WithRoom(room);
        map.Save(path);

        int slabs = UnrealLevelExporter.BuildSlabs(room).Count();
        return $"Added room '{room.Name}' at {room.Center}, size {room.Size} "
             + $"({(room.Hollow ? "hollow" : "solid")}, {slabs} slab{(slabs == 1 ? "" : "s")}). "
             + $"Map now has {map.Rooms.Count} room{(map.Rooms.Count == 1 ? "" : "s")}.";
    }

    private string MapAddLight(JsonObject arguments)
    {
        string path = ResolvePath(RequireString(arguments, "map"));
        MapDefinition map = MapDefinition.Load(path);

        string kindText = OptionalString(arguments, "kind") ?? nameof(LightKind.Point);
        if (!Enum.TryParse(kindText, ignoreCase: true, out LightKind kind))
            throw new ArgumentException($"Unknown light kind '{kindText}'. Use Point, Spot, Directional or Rect.");

        Vec3? colour = ReadVec3(arguments, "color");
        var light = new MapLight
        {
            Name = RequireString(arguments, "name"),
            Kind = kind,
            Position = ReadVec3(arguments, "position") ?? Vec3.Zero,
            Rotation = ReadRotator(arguments, "rotation"),
            Intensity = OptionalDouble(arguments, "intensity") ?? 5000,
            Color = colour is { } c ? new Rgb(c.X, c.Y, c.Z) : Rgb.White,
            Radius = OptionalDouble(arguments, "radius") ?? 1000,
            ConeAngle = OptionalDouble(arguments, "coneAngle") ?? 44,
            CastShadows = OptionalBool(arguments, "castShadows") ?? true,
            Dynamic = OptionalBool(arguments, "dynamic") ?? true,
        };

        map = map.WithLight(light);
        map.Save(path);

        return $"Added {light.Kind} light '{light.Name}' at {light.Position}, "
             + $"intensity {light.Intensity:0.##}, {(light.Dynamic ? "movable" : "static")}. "
             + $"Map now has {map.Lights.Count} light{(map.Lights.Count == 1 ? "" : "s")}.";
    }

    private string MapAddProp(JsonObject arguments)
    {
        string path = ResolvePath(RequireString(arguments, "map"));
        MapDefinition map = MapDefinition.Load(path);

        var prop = new MapProp
        {
            Name = RequireString(arguments, "name"),
            Mesh = RequireString(arguments, "mesh"),
            Position = ReadVec3(arguments, "position") ?? Vec3.Zero,
            Rotation = ReadRotator(arguments, "rotation"),
            Scale = ReadVec3(arguments, "scale") ?? new Vec3(1, 1, 1),
            Material = OptionalString(arguments, "material"),
        };

        map = map.WithProp(prop);
        map.Save(path);

        return $"Placed '{prop.Name}' ({prop.Mesh}) at {prop.Position}. "
             + $"Map now has {map.Props.Count} prop{(map.Props.Count == 1 ? "" : "s")}.";
    }

    private string MapDescribe(JsonObject arguments)
    {
        string path = ResolvePath(RequireString(arguments, "map"));
        MapDefinition map = MapDefinition.Load(path);
        (Vec3 Min, Vec3 Max)? bounds = map.Bounds();

        return new JsonObject
        {
            ["name"] = map.Name,
            ["description"] = map.Description,
            ["path"] = path,
            ["playerStart"] = map.PlayerStart.ToString(),
            ["bounds"] = bounds is { } b ? $"{b.Min} to {b.Max}" : null,
            ["roomCount"] = map.Rooms.Count,
            ["slabCount"] = map.Rooms.Sum(r => UnrealLevelExporter.BuildSlabs(r).Count()),
            ["lightCount"] = map.Lights.Count,
            ["propCount"] = map.Props.Count,
            ["environment"] = new JsonObject
            {
                ["lumenGlobalIllumination"] = map.Environment.LumenGlobalIllumination,
                ["lumenReflections"] = map.Environment.LumenReflections,
                ["hardwareRayTracing"] = map.Environment.HardwareRayTracing,
                ["lumenQuality"] = map.Environment.LumenQuality,
                ["daylight"] = map.Environment.Daylight,
            },
            ["rooms"] = new JsonArray([.. map.Rooms.Select(r => (JsonNode)new JsonObject
            {
                ["name"] = r.Name,
                ["center"] = r.Center.ToString(),
                ["size"] = r.Size.ToString(),
                ["hollow"] = r.Hollow,
            })]),
            ["lights"] = new JsonArray([.. map.Lights.Select(l => (JsonNode)new JsonObject
            {
                ["name"] = l.Name,
                ["kind"] = l.Kind.ToString(),
                ["position"] = l.Position.ToString(),
                ["intensity"] = l.Intensity,
            })]),
        }.ToJsonString(GameStudioJson.Options);
    }

    private string ExportMapUnreal(JsonObject arguments)
    {
        string path = ResolvePath(RequireString(arguments, "map"));
        string output = ResolvePath(RequireString(arguments, "output"));
        MapDefinition map = MapDefinition.Load(path);

        UnrealLevelExportResult result = UnrealLevelExporter.Export(
            map, output, OptionalString(arguments, "contentRoot") ?? "/Game/SeriousEngine");

        return $"Exported '{map.Name}': {result.SlabCount} slabs, {result.LightCount} lights, "
             + $"{result.PropCount} props.\nScript: {result.ScriptPath}\nManifest: {result.ManifestPath}\n"
             + "Run it from the Unreal editor with: py \"" + result.ScriptPath + "\"";
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

    private static double? OptionalDouble(JsonObject arguments, string name) =>
        arguments[name] is { } node && node.GetValueKind() == JsonValueKind.Number ? node.GetValue<double>() : null;

    /// <summary>Reads a <c>[x, y, z]</c> array, or null when the argument is absent.</summary>
    private static Vec3? ReadVec3(JsonObject arguments, string name)
    {
        if (arguments[name] is not JsonArray array) return null;
        if (array.Count != 3)
            throw new ArgumentException($"'{name}' must have exactly three numbers, got {array.Count}.");

        double At(int i) => array[i]?.GetValueKind() == JsonValueKind.Number
            ? array[i]!.GetValue<double>()
            : throw new ArgumentException($"'{name}' element {i} is not a number.");

        return new Vec3(At(0), At(1), At(2));
    }

    /// <summary>Reads a <c>[pitch, yaw, roll]</c> array, defaulting to no rotation.</summary>
    private static Rotator ReadRotator(JsonObject arguments, string name) =>
        ReadVec3(arguments, name) is { } v ? new Rotator(v.X, v.Y, v.Z) : new Rotator();

    private static string[] ReadStrings(JsonObject arguments, string name)
    {
        if (arguments[name] is not JsonArray array) return [];
        return [.. array.Select((node, i) => node?.GetValueKind() == JsonValueKind.String
            ? node!.GetValue<string>()
            : throw new ArgumentException($"'{name}' element {i} is not a string."))];
    }

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
