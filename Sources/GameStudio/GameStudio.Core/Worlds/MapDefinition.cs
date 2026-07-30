using System.Text.Json;

namespace GameStudio.Core.Worlds;

/// <summary>A position or size in world units. Unreal's units are centimetres.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public override string ToString() => $"({X:0.##}, {Y:0.##}, {Z:0.##})";
}

/// <summary>Yaw/pitch/roll in degrees.</summary>
public readonly record struct Rotator(double Pitch = 0, double Yaw = 0, double Roll = 0);

/// <summary>Linear colour, each channel 0..1.</summary>
public readonly record struct Rgb(double R, double G, double B)
{
    public static readonly Rgb White = new(1, 1, 1);
}

public enum LightKind { Point, Spot, Directional, Rect }

/// <summary>
/// A box-shaped space. <see cref="Hollow"/> is what separates a room you can stand in from a solid
/// block: hollow boxes become six inward-facing slabs, solid ones a single filled volume.
/// </summary>
public sealed record MapRoom
{
    public required string Name { get; init; }
    public Vec3 Center { get; init; } = Vec3.Zero;

    /// <summary>Outer dimensions. For a hollow room this is the space including its shell.</summary>
    public Vec3 Size { get; init; } = new(800, 800, 400);

    public bool Hollow { get; init; } = true;

    /// <summary>Shell thickness for hollow rooms, in world units.</summary>
    public double WallThickness { get; init; } = 20;

    /// <summary>Material asset name to apply, resolved against the export's content root.</summary>
    public string? Material { get; init; }

    /// <summary>Faces to leave open on a hollow room, so rooms can connect: Floor, Ceiling, North, South, East, West.</summary>
    public IReadOnlyList<string> OpenFaces { get; init; } = [];
}

public sealed record MapLight
{
    public required string Name { get; init; }
    public LightKind Kind { get; init; } = LightKind.Point;
    public Vec3 Position { get; init; } = Vec3.Zero;
    public Rotator Rotation { get; init; } = new();

    /// <summary>Candelas for point/spot lights, lux for directional ones.</summary>
    public double Intensity { get; init; } = 5000;

    public Rgb Color { get; init; } = Rgb.White;

    /// <summary>Attenuation radius in world units. Ignored by directional lights.</summary>
    public double Radius { get; init; } = 1000;

    /// <summary>Outer cone angle in degrees, spot lights only.</summary>
    public double ConeAngle { get; init; } = 44;

    public bool CastShadows { get; init; } = true;

    /// <summary>
    /// Marks the light as stationary/movable so Lumen lights it dynamically rather than baking it.
    /// Static lights are cheaper but do not participate in ray-traced bounces.
    /// </summary>
    public bool Dynamic { get; init; } = true;
}

public sealed record MapProp
{
    public required string Name { get; init; }

    /// <summary>Static mesh asset path, e.g. <c>/Engine/BasicShapes/Cylinder</c> or an imported mesh.</summary>
    public required string Mesh { get; init; }

    public Vec3 Position { get; init; } = Vec3.Zero;
    public Rotator Rotation { get; init; } = new();
    public Vec3 Scale { get; init; } = new(1, 1, 1);
    public string? Material { get; init; }
}

/// <summary>
/// Rendering settings written into the level's post-process volume. These are the switches that
/// decide whether the level renders with ray-traced global illumination and reflections or with
/// the older screen-space approximations.
/// </summary>
public sealed record MapEnvironment
{
    /// <summary>Lumen global illumination — ray-traced indirect lighting.</summary>
    public bool LumenGlobalIllumination { get; init; } = true;

    /// <summary>Lumen reflections, which trace rays against the scene rather than the screen.</summary>
    public bool LumenReflections { get; init; } = true;

    /// <summary>Ask Lumen to trace against real triangles instead of the distance-field proxies.</summary>
    public bool HardwareRayTracing { get; init; } = true;

    /// <summary>Lumen quality multiplier. 1 is the engine default; 2 roughly doubles the ray budget.</summary>
    public double LumenQuality { get; init; } = 2.0;

    /// <summary>Exposure compensation in stops applied to the whole level.</summary>
    public double ExposureCompensation { get; init; } = 0;

    /// <summary>Spawn a directional light plus sky atmosphere and a sky light.</summary>
    public bool Daylight { get; init; } = true;

    /// <summary>Sun elevation in degrees above the horizon, used when <see cref="Daylight"/> is set.</summary>
    public double SunElevation { get; init; } = 45;
}

/// <summary>
/// A whole level, described declaratively so it can be authored a piece at a time — by an MCP
/// client, by hand, or by a generator — and only turned into engine data at export.
/// <para>
/// This is deliberately not a Serious Engine <c>.wld</c>. That format carries BSP trees, sector
/// topology and per-entity-class property serialization that only the original editor can produce
/// correctly. Targeting Unreal instead keeps map authoring honest and is what makes ray-traced
/// lighting available at all.
/// </para>
/// </summary>
public sealed record MapDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    public Vec3 PlayerStart { get; init; } = new(0, 0, 100);
    public double PlayerStartYaw { get; init; }

    public MapEnvironment Environment { get; init; } = new();

    public IReadOnlyList<MapRoom> Rooms { get; init; } = [];
    public IReadOnlyList<MapLight> Lights { get; init; } = [];
    public IReadOnlyList<MapProp> Props { get; init; } = [];

    public static MapDefinition Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<MapDefinition>(stream, GameStudioJson.Options)
               ?? throw new InvalidDataException($"'{path}' does not contain a map definition.");
    }

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(this, GameStudioJson.Options));
    }

    public MapDefinition WithRoom(MapRoom room) => this with { Rooms = [.. Rooms, room] };
    public MapDefinition WithLight(MapLight light) => this with { Lights = [.. Lights, light] };
    public MapDefinition WithProp(MapProp prop) => this with { Props = [.. Props, prop] };

    /// <summary>
    /// Axis-aligned bounds covering every room, or null for a map with no rooms yet. Reported by
    /// the describe tool so a client authoring blind can tell where it has already built.
    /// </summary>
    public (Vec3 Min, Vec3 Max)? Bounds()
    {
        if (Rooms.Count == 0) return null;

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

        foreach (MapRoom room in Rooms)
        {
            Vec3 half = room.Size * 0.5;
            minX = Math.Min(minX, room.Center.X - half.X);
            minY = Math.Min(minY, room.Center.Y - half.Y);
            minZ = Math.Min(minZ, room.Center.Z - half.Z);
            maxX = Math.Max(maxX, room.Center.X + half.X);
            maxY = Math.Max(maxY, room.Center.Y + half.Y);
            maxZ = Math.Max(maxZ, room.Center.Z + half.Z);
        }

        return (new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ));
    }
}
