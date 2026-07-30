using System.Text.Json;
using System.Text.Json.Nodes;
using GameStudio.Core.Worlds;
using GameStudio.Mcp;

namespace GameStudio.Core.Tests;

public class MapGeometryTests
{
    private static MapRoom Room(bool hollow = true, double thickness = 20) => new()
    {
        Name = "Hall",
        Center = new Vec3(0, 0, 0),
        Size = new Vec3(800, 600, 400),
        Hollow = hollow,
        WallThickness = thickness,
    };

    [Fact]
    public void SolidRoom_IsASingleSlabMatchingTheRoom()
    {
        LevelSlab slab = Assert.Single(UnrealLevelExporter.BuildSlabs(Room(hollow: false)));

        Assert.Equal("Hall", slab.Name);
        Assert.Equal(new Vec3(0, 0, 0), slab.Center);
        Assert.Equal(new Vec3(800, 600, 400), slab.Size);
    }

    [Fact]
    public void HollowRoom_IsAShellOfSixSlabs()
    {
        var slabs = UnrealLevelExporter.BuildSlabs(Room()).ToList();

        Assert.Equal(
            ["Hall_Floor", "Hall_Ceiling", "Hall_South", "Hall_North", "Hall_West", "Hall_East"],
            slabs.Select(s => s.Name));
    }

    [Fact]
    public void HollowRoom_PlacesTheFloorSlabInsideTheLowerBound()
    {
        LevelSlab floor = UnrealLevelExporter.BuildSlabs(Room()).First(s => s.Name.EndsWith("_Floor"));

        // Room spans z -200..200; a 20-thick floor sits centred at -190, so its top face is at -180.
        Assert.Equal(-190d, floor.Center.Z, precision: 6);
        Assert.Equal(20d, floor.Size.Z, precision: 6);
        Assert.Equal(800d, floor.Size.X, precision: 6);
    }

    [Fact]
    public void HollowRoom_InsetsTheYWallsBetweenTheXWallsSoNoTwoSlabsOverlap()
    {
        var slabs = UnrealLevelExporter.BuildSlabs(Room()).ToDictionary(s => s.Name);

        // X-facing walls span the full depth; Y-facing ones are shortened by a wall at each end.
        Assert.Equal(600d, slabs["Hall_North"].Size.Y, precision: 6);
        Assert.Equal(760d, slabs["Hall_West"].Size.X, precision: 6);

        // Both wall sets stop short of floor and ceiling.
        Assert.Equal(360d, slabs["Hall_North"].Size.Z, precision: 6);
        Assert.Equal(360d, slabs["Hall_West"].Size.Z, precision: 6);
    }

    [Fact]
    public void OpenFaces_AreOmittedFromTheShell()
    {
        MapRoom room = Room() with { OpenFaces = ["ceiling", "North"] };
        var names = UnrealLevelExporter.BuildSlabs(room).Select(s => s.Name).ToList();

        Assert.DoesNotContain("Hall_Ceiling", names);   // matched case-insensitively
        Assert.DoesNotContain("Hall_North", names);
        Assert.Equal(4, names.Count);
    }

    [Fact]
    public void WallsTooThickForTheRoom_FallBackToASolidBlock()
    {
        // 200-thick walls cannot form a shell in a 400-tall room: two of them fill it exactly.
        LevelSlab slab = Assert.Single(UnrealLevelExporter.BuildSlabs(Room(thickness: 200)));
        Assert.Equal("Hall", slab.Name);
    }

    [Fact]
    public void Bounds_CoverEveryRoom()
    {
        var map = new MapDefinition { Name = "Two" }
            .WithRoom(new MapRoom { Name = "A", Center = new Vec3(0, 0, 0), Size = new Vec3(100, 100, 100) })
            .WithRoom(new MapRoom { Name = "B", Center = new Vec3(500, 0, 0), Size = new Vec3(100, 100, 100) });

        (Vec3 Min, Vec3 Max) bounds = map.Bounds()!.Value;

        Assert.Equal(-50d, bounds.Min.X, precision: 6);
        Assert.Equal(550d, bounds.Max.X, precision: 6);
    }

    [Fact]
    public void Bounds_AreNullBeforeAnyRoomExists() =>
        Assert.Null(new MapDefinition { Name = "Empty" }.Bounds());
}

public class MapAuthoringToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameStudioMapTests", Guid.NewGuid().ToString("N"));

    public MapAuthoringToolTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string MapPath => Path.Combine(_root, "arena.map.json");

    private async Task<string> CallAsync(string name, object arguments)
    {
        var catalog = new ToolCatalog(_root);
        JsonNode result = await catalog.CallAsync(
            name,
            JsonNode.Parse(JsonSerializer.Serialize(arguments))!.AsObject(),
            CancellationToken.None);

        Assert.False(result["isError"]!.GetValue<bool>(),
            result["content"]![0]!["text"]!.GetValue<string>());
        return result["content"]![0]!["text"]!.GetValue<string>();
    }

    [Fact]
    public async Task AuthoringAMap_AccumulatesAcrossCallsAndExports()
    {
        await CallAsync("create_map", new { map = MapPath, name = "Arena", playerStart = new[] { 0, 0, 120 } });
        await CallAsync("map_add_room", new
        {
            map = MapPath,
            name = "Main",
            center = new[] { 0, 0, 0 },
            size = new[] { 2000, 2000, 600 },
        });
        await CallAsync("map_add_light", new
        {
            map = MapPath,
            name = "Key",
            kind = "Spot",
            position = new[] { 0, 0, 250 },
            intensity = 12000,
            color = new[] { 1.0, 0.9, 0.8 },
        });
        await CallAsync("map_add_prop", new
        {
            map = MapPath,
            name = "Pillar",
            mesh = "/Engine/BasicShapes/Cylinder",
            position = new[] { 300, 0, 0 },
        });

        MapDefinition map = MapDefinition.Load(MapPath);
        Assert.Equal("Arena", map.Name);
        Assert.Equal(new Vec3(0, 0, 120), map.PlayerStart);
        Assert.Equal("Main", Assert.Single(map.Rooms).Name);
        Assert.Equal(LightKind.Spot, Assert.Single(map.Lights).Kind);
        Assert.Equal("Pillar", Assert.Single(map.Props).Name);

        string output = Path.Combine(_root, "unreal");
        string report = await CallAsync("export_map_unreal", new { map = MapPath, output });

        Assert.Contains("6 slabs", report);
        Assert.True(File.Exists(Path.Combine(output, "build_level.py")));

        JsonNode manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "level.json")))!;
        Assert.Equal("Arena", manifest["name"]!.GetValue<string>());
        Assert.Equal(6, manifest["slabs"]!.AsArray().Count);
        Assert.Equal("Spot", manifest["lights"]![0]!["kind"]!.GetValue<string>());
        Assert.True(manifest["environment"]!["hardwareRayTracing"]!.GetValue<bool>());

        // The generated script reads these keys back out; a rename on either side breaks the level.
        string script = File.ReadAllText(Path.Combine(output, "build_level.py"));
        Assert.Contains("manifest[\"slabs\"]", script);
        Assert.Contains("lumen_final_gather_quality", script);
    }

    [Fact]
    public async Task CreateMap_RefusesToClobberAnExistingFileUnlessAsked()
    {
        await CallAsync("create_map", new { map = MapPath, name = "First" });

        var catalog = new ToolCatalog(_root);
        JsonNode refused = await catalog.CallAsync(
            "create_map",
            JsonNode.Parse(JsonSerializer.Serialize(new { map = MapPath, name = "Second" }))!.AsObject(),
            CancellationToken.None);
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Equal("First", MapDefinition.Load(MapPath).Name);

        await CallAsync("create_map", new { map = MapPath, name = "Second", overwrite = true });
        Assert.Equal("Second", MapDefinition.Load(MapPath).Name);
    }

    [Fact]
    public async Task PathsOutsideTheWorkspaceRootAreRefused()
    {
        var catalog = new ToolCatalog(_root);
        JsonNode result = await catalog.CallAsync(
            "create_map",
            JsonNode.Parse(JsonSerializer.Serialize(new
            {
                map = Path.Combine(Path.GetTempPath(), "escaped.map.json"),
                name = "Escape",
            }))!.AsObject(),
            CancellationToken.None);

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("outside the workspace root", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnUnknownLightKindIsReportedRatherThanSilentlyDefaulted()
    {
        await CallAsync("create_map", new { map = MapPath, name = "Arena" });

        var catalog = new ToolCatalog(_root);
        JsonNode result = await catalog.CallAsync(
            "map_add_light",
            JsonNode.Parse(JsonSerializer.Serialize(new { map = MapPath, name = "Bad", kind = "Lantern" }))!.AsObject(),
            CancellationToken.None);

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("Lantern", result["content"]![0]!["text"]!.GetValue<string>());
    }
}
