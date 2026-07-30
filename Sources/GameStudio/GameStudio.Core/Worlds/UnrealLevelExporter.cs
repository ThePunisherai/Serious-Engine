using System.Text.Json;

namespace GameStudio.Core.Worlds;

/// <summary>One axis-aligned box of level geometry, ready to become a scaled cube actor.</summary>
public sealed record LevelSlab(string Name, Vec3 Center, Vec3 Size, string? Material);

public sealed record UnrealLevelExportResult(
    string ScriptPath,
    string ManifestPath,
    int SlabCount,
    int LightCount,
    int PropCount);

/// <summary>
/// Turns a <see cref="MapDefinition"/> into an Unreal Engine 5 level package: a manifest of every
/// actor to spawn plus a Python script Unreal runs in-editor to build the level.
/// <para>
/// The decomposition of rooms into slabs happens here rather than in the script, so the geometry is
/// testable without an Unreal install. The script stays a thin executor of what the manifest says.
/// </para>
/// </summary>
public static class UnrealLevelExporter
{
    /// <summary>Side length of Unreal's built-in cube mesh, which every slab is a scaled copy of.</summary>
    private const double BasicCubeSize = 100.0;

    private const string CubeMesh = "/Engine/BasicShapes/Cube";

    public static UnrealLevelExportResult Export(MapDefinition map, string outputDirectory,
        string contentRoot = "/Game/SeriousEngine")
    {
        ArgumentNullException.ThrowIfNull(map);
        Directory.CreateDirectory(outputDirectory);

        List<LevelSlab> slabs = [.. map.Rooms.SelectMany(BuildSlabs)];

        string manifestPath = Path.Combine(outputDirectory, "level.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            name = map.Name,
            description = map.Description,
            contentRoot,
            cubeMesh = CubeMesh,
            cubeSize = BasicCubeSize,
            generatedAt = DateTimeOffset.UtcNow,
            playerStart = new { position = map.PlayerStart, yaw = map.PlayerStartYaw },
            environment = map.Environment,
            slabs,
            lights = map.Lights,
            props = map.Props,
        }, GameStudioJson.Options));

        string scriptPath = Path.Combine(outputDirectory, "build_level.py");
        File.WriteAllText(scriptPath, BuildScript);

        return new UnrealLevelExportResult(scriptPath, manifestPath, slabs.Count, map.Lights.Count, map.Props.Count);
    }

    /// <summary>
    /// Decomposes a room into its slabs. A solid room is a single box; a hollow one is a shell of
    /// six, with the X-facing walls spanning the full depth and the Y-facing walls inset between
    /// them so no two slabs occupy the same space.
    /// </summary>
    public static IEnumerable<LevelSlab> BuildSlabs(MapRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);

        if (!room.Hollow)
        {
            yield return new LevelSlab(room.Name, room.Center, room.Size, room.Material);
            yield break;
        }

        double t = room.WallThickness;
        Vec3 c = room.Center;
        Vec3 s = room.Size;

        // A shell needs room for two opposing walls; below that the room has no interior at all.
        if (t * 2 >= Math.Min(s.X, Math.Min(s.Y, s.Z)))
        {
            yield return new LevelSlab(room.Name, c, s, room.Material);
            yield break;
        }

        double innerHeight = s.Z - (t * 2);

        foreach ((string face, Vec3 center, Vec3 size) in new[]
        {
            ("Floor",   new Vec3(c.X, c.Y, c.Z - (s.Z / 2) + (t / 2)), new Vec3(s.X, s.Y, t)),
            ("Ceiling", new Vec3(c.X, c.Y, c.Z + (s.Z / 2) - (t / 2)), new Vec3(s.X, s.Y, t)),
            ("South",   new Vec3(c.X - (s.X / 2) + (t / 2), c.Y, c.Z), new Vec3(t, s.Y, innerHeight)),
            ("North",   new Vec3(c.X + (s.X / 2) - (t / 2), c.Y, c.Z), new Vec3(t, s.Y, innerHeight)),
            ("West",    new Vec3(c.X, c.Y - (s.Y / 2) + (t / 2), c.Z), new Vec3(s.X - (t * 2), t, innerHeight)),
            ("East",    new Vec3(c.X, c.Y + (s.Y / 2) - (t / 2), c.Z), new Vec3(s.X - (t * 2), t, innerHeight)),
        })
        {
            if (room.OpenFaces.Any(f => string.Equals(f, face, StringComparison.OrdinalIgnoreCase))) continue;
            yield return new LevelSlab($"{room.Name}_{face}", center, size, room.Material);
        }
    }

    private const string BuildScript = """
        # Builds a GameStudio level inside Unreal Engine 5.
        #
        # Run from the Unreal editor with this directory on the path:
        #   py "<this directory>/build_level.py"
        #
        # Everything spawned is described by level.json next to this script. Re-running replaces the
        # actors it created previously, which are tagged "GameStudio" so hand-placed actors survive.

        import json
        import os

        import unreal

        TAG = "GameStudio"

        LIGHT_CLASSES = {
            "Point": unreal.PointLight,
            "Spot": unreal.SpotLight,
            "Directional": unreal.DirectionalLight,
            "Rect": unreal.RectLight,
        }


        def load_manifest():
            path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "level.json")
            with open(path, "r") as handle:
                return json.load(handle)


        def vec(value):
            return unreal.Vector(value["x"], value["y"], value["z"])


        def clear_previous():
            subsystem = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)
            for actor in subsystem.get_all_level_actors():
                if actor.actor_has_tag(TAG):
                    subsystem.destroy_actor(actor)


        def spawn(actor_class, location, rotation=None):
            subsystem = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)
            actor = subsystem.spawn_actor_from_class(
                actor_class, location, rotation or unreal.Rotator(0, 0, 0))
            actor.tags = [TAG]
            return actor


        def load_material(content_root, name):
            if not name:
                return None
            path = name if name.startswith("/") else "{0}/{1}".format(content_root, name)
            if not unreal.EditorAssetLibrary.does_asset_exist(path):
                unreal.log_warning("Material not found, leaving default: {0}".format(path))
                return None
            return unreal.EditorAssetLibrary.load_asset(path)


        def build_slabs(manifest):
            mesh = unreal.EditorAssetLibrary.load_asset(manifest["cubeMesh"])
            cube_size = manifest["cubeSize"]
            content_root = manifest["contentRoot"]

            for slab in manifest["slabs"]:
                actor = spawn(unreal.StaticMeshActor, vec(slab["center"]))
                actor.set_actor_label(slab["name"])

                component = actor.static_mesh_component
                component.set_static_mesh(mesh)
                size = slab["size"]
                actor.set_actor_scale3d(unreal.Vector(
                    size["x"] / cube_size, size["y"] / cube_size, size["z"] / cube_size))

                material = load_material(content_root, slab.get("material"))
                if material:
                    component.set_material(0, material)


        def build_lights(manifest):
            for light in manifest["lights"]:
                actor_class = LIGHT_CLASSES.get(light["kind"], unreal.PointLight)
                rotation = unreal.Rotator(
                    light["rotation"]["roll"], light["rotation"]["pitch"], light["rotation"]["yaw"])
                actor = spawn(actor_class, vec(light["position"]), rotation)
                actor.set_actor_label(light["name"])

                component = actor.light_component
                colour = light["color"]
                component.set_light_color(
                    unreal.LinearColor(colour["r"], colour["g"], colour["b"], 1.0), srgb=False)
                component.set_editor_property("intensity", light["intensity"])
                component.set_editor_property("cast_shadows", light["castShadows"])
                component.set_editor_property(
                    "mobility",
                    unreal.ComponentMobility.MOVABLE if light["dynamic"]
                    else unreal.ComponentMobility.STATIC)

                if light["kind"] in ("Point", "Spot", "Rect"):
                    component.set_editor_property("attenuation_radius", light["radius"])
                if light["kind"] == "Spot":
                    component.set_editor_property("outer_cone_angle", light["coneAngle"])


        def build_props(manifest):
            content_root = manifest["contentRoot"]
            for prop in manifest["props"]:
                mesh = unreal.EditorAssetLibrary.load_asset(prop["mesh"])
                if mesh is None:
                    unreal.log_warning("Prop mesh missing: {0}".format(prop["mesh"]))
                    continue

                rotation = unreal.Rotator(
                    prop["rotation"]["roll"], prop["rotation"]["pitch"], prop["rotation"]["yaw"])
                actor = spawn(unreal.StaticMeshActor, vec(prop["position"]), rotation)
                actor.set_actor_label(prop["name"])
                actor.static_mesh_component.set_static_mesh(mesh)
                actor.set_actor_scale3d(vec(prop["scale"]))

                material = load_material(content_root, prop.get("material"))
                if material:
                    actor.static_mesh_component.set_material(0, material)


        def build_environment(manifest):
            environment = manifest["environment"]

            # Post-process volume carrying the ray-tracing settings. Unbound so it covers the level.
            volume = spawn(unreal.PostProcessVolume, unreal.Vector(0, 0, 0))
            volume.set_actor_label("GameStudio_PostProcess")
            volume.set_editor_property("unbound", True)

            settings = volume.settings
            settings.set_editor_property("override_dynamic_global_illumination_method", True)
            settings.set_editor_property(
                "dynamic_global_illumination_method",
                unreal.DynamicGlobalIlluminationMethod.LUMEN if environment["lumenGlobalIllumination"]
                else unreal.DynamicGlobalIlluminationMethod.NONE)

            settings.set_editor_property("override_reflection_method", True)
            settings.set_editor_property(
                "reflection_method",
                unreal.ReflectionMethod.LUMEN if environment["lumenReflections"]
                else unreal.ReflectionMethod.SCREEN_SPACE)

            settings.set_editor_property("override_lumen_final_gather_quality", True)
            settings.set_editor_property("lumen_final_gather_quality", environment["lumenQuality"])

            settings.set_editor_property("override_lumen_ray_lighting_mode", True)
            settings.set_editor_property(
                "lumen_ray_lighting_mode",
                unreal.LumenRayLightingModeOverride.HIT_LIGHTING if environment["hardwareRayTracing"]
                else unreal.LumenRayLightingModeOverride.SURFACE_CACHE)

            settings.set_editor_property("override_auto_exposure_bias", True)
            settings.set_editor_property("auto_exposure_bias", environment["exposureCompensation"])
            volume.settings = settings

            if not environment["daylight"]:
                return

            sun = spawn(unreal.DirectionalLight, unreal.Vector(0, 0, 1000),
                        unreal.Rotator(0, -environment["sunElevation"], 0))
            sun.set_actor_label("GameStudio_Sun")
            sun.light_component.set_editor_property("mobility", unreal.ComponentMobility.MOVABLE)

            sky_light = spawn(unreal.SkyLight, unreal.Vector(0, 0, 1000))
            sky_light.set_actor_label("GameStudio_SkyLight")
            sky_light.light_component.set_editor_property("mobility", unreal.ComponentMobility.MOVABLE)
            sky_light.light_component.set_editor_property("real_time_capture", True)

            atmosphere = spawn(unreal.SkyAtmosphere, unreal.Vector(0, 0, 0))
            atmosphere.set_actor_label("GameStudio_SkyAtmosphere")


        def build_player_start(manifest):
            start = manifest["playerStart"]
            actor = spawn(unreal.PlayerStart, vec(start["position"]),
                          unreal.Rotator(0, 0, start["yaw"]))
            actor.set_actor_label("GameStudio_PlayerStart")


        def main():
            manifest = load_manifest()
            unreal.log("Building level '{0}'".format(manifest["name"]))

            clear_previous()
            build_environment(manifest)
            build_slabs(manifest)
            build_lights(manifest)
            build_props(manifest)
            build_player_start(manifest)

            unreal.log("Level '{0}' built: {1} slabs, {2} lights, {3} props".format(
                manifest["name"], len(manifest["slabs"]), len(manifest["lights"]),
                len(manifest["props"])))


        main()
        """;
}
