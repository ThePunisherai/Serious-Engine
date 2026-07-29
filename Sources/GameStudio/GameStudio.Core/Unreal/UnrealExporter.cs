using System.Text;
using System.Text.Json;
using GameStudio.Core.Modernize;

namespace GameStudio.Core.Unreal;

public sealed record UnrealExportSettings
{
    /// <summary>Content-browser folder the assets land in, e.g. <c>/Game/SeriousEngine</c>.</summary>
    public string ContentRoot { get; init; } = "/Game/SeriousEngine";

    /// <summary>Prefix applied to generated Material assets, following Unreal's naming convention.</summary>
    public string MaterialPrefix { get; init; } = "M_";

    /// <summary>Prefix applied to imported Texture2D assets.</summary>
    public string TexturePrefix { get; init; } = "T_";

    /// <summary>Pack roughness/AO into a single ORM-style texture reference in the material graph.</summary>
    public bool UseTwoSidedForMaskedMaterials { get; init; } = true;

    /// <summary>Emit materials that use masked blending when the source texture had alpha.</summary>
    public bool RespectAlphaAsMask { get; init; } = true;
}

public sealed record UnrealExportResult(string ScriptPath, string ManifestPath, int MaterialCount, int TextureCount);

/// <summary>
/// Turns a completed <see cref="ModernizeReport"/> into an Unreal Engine 5 import package: a
/// manifest of every generated map plus a Python script that Unreal runs in-editor to import the
/// textures and build a wired material for each one.
/// <para>
/// The script is generated rather than executed here — it runs inside Unreal's own Python
/// environment, which is the only place the asset APIs exist.
/// </para>
/// </summary>
public static class UnrealExporter
{
    public static UnrealExportResult Export(ModernizeReport report, string outputDirectory,
        UnrealExportSettings? settings = null)
    {
        settings ??= new UnrealExportSettings();
        Directory.CreateDirectory(outputDirectory);

        var materials = report.Textures.Select(t => BuildMaterialSpec(t, settings)).ToList();

        string manifestPath = Path.Combine(outputDirectory, "unreal_import.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            contentRoot = settings.ContentRoot,
            sourceRoot = report.OutputDirectory,
            generatedAt = DateTimeOffset.UtcNow,
            materials,
        }, GameStudioJson.Options));

        string scriptPath = Path.Combine(outputDirectory, "import_to_unreal.py");
        File.WriteAllText(scriptPath, BuildImportScript(), new UTF8Encoding(false));

        return new UnrealExportResult(scriptPath, manifestPath, materials.Count, materials.Sum(m => m.Textures.Count));
    }

    private static MaterialSpec BuildMaterialSpec(ModernizedTexture texture, UnrealExportSettings settings)
    {
        var textures = new List<TextureSpec>();

        foreach (var (role, relativePath) in texture.Maps)
        {
            // Animated frames land as BaseColor000, Normal000, …; only the first frame drives
            // the material graph, the rest are imported as plain assets for a flipbook.
            string baseRole = new(role.TakeWhile(char.IsLetter).ToArray());
            bool isPrimaryFrame = baseRole.Length == role.Length;

            textures.Add(new TextureSpec(
                Role: role,
                BaseRole: baseRole,
                AssetName: settings.TexturePrefix + Sanitize(Path.GetFileNameWithoutExtension(relativePath)),
                SourceFile: relativePath,
                // Only base colour carries sRGB-encoded data; the derived maps are linear.
                Srgb: baseRole == "BaseColor",
                CompressionSettings: CompressionFor(baseRole),
                ConnectToMaterial: isPrimaryFrame));
        }

        return new MaterialSpec(
            MaterialName: settings.MaterialPrefix + Sanitize(texture.MaterialName),
            SourceAsset: texture.SourcePath,
            BlendMode: settings.RespectAlphaAsMask && texture.HasAlpha ? "MASKED" : "OPAQUE",
            TwoSided: settings.UseTwoSidedForMaskedMaterials && texture.HasAlpha,
            Textures: textures);
    }

    private static string CompressionFor(string role) => role switch
    {
        "Normal" => "TC_NORMALMAP",
        "Roughness" or "AmbientOcclusion" or "Metallic" or "Height" => "TC_MASKS",
        _ => "TC_DEFAULT",
    };

    private static string Sanitize(string name) => ModernizePipeline.SanitizeName(name);

    private sealed record TextureSpec(string Role, string BaseRole, string AssetName, string SourceFile,
        bool Srgb, string CompressionSettings, bool ConnectToMaterial);

    private sealed record MaterialSpec(string MaterialName, string SourceAsset, string BlendMode,
        bool TwoSided, List<TextureSpec> Textures);

    /// <summary>
    /// The generated script reads the manifest that sits next to it, so re-running a modernization
    /// pass only requires re-running the script — the script itself never needs regenerating.
    /// </summary>
    private static string BuildImportScript() => """"
        """
        GameStudio Engine -> Unreal Engine 5 importer.

        Run from inside the Unreal Editor:
            py "<path to this file>"

        Reads unreal_import.json from the same directory, imports every generated texture as a
        Texture2D with the right sRGB and compression settings, and builds a material per source
        texture with BaseColor / Normal / Roughness / AmbientOcclusion wired up.
        """
        import json
        import os

        import unreal

        SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
        MANIFEST = os.path.join(SCRIPT_DIR, "unreal_import.json")

        # Where each map role attaches on the material output node.
        MATERIAL_INPUTS = {
            "BaseColor": unreal.MaterialProperty.MP_BASE_COLOR,
            "Normal": unreal.MaterialProperty.MP_NORMAL,
            "Roughness": unreal.MaterialProperty.MP_ROUGHNESS,
            "AmbientOcclusion": unreal.MaterialProperty.MP_AMBIENT_OCCLUSION,
            "Metallic": unreal.MaterialProperty.MP_METALLIC,
        }

        COMPRESSION = {
            "TC_DEFAULT": unreal.TextureCompressionSettings.TC_DEFAULT,
            "TC_NORMALMAP": unreal.TextureCompressionSettings.TC_NORMALMAP,
            "TC_MASKS": unreal.TextureCompressionSettings.TC_MASKS,
        }

        BLEND_MODES = {
            "OPAQUE": unreal.BlendMode.BLEND_OPAQUE,
            "MASKED": unreal.BlendMode.BLEND_MASKED,
        }


        def load_manifest():
            with open(MANIFEST, "r", encoding="utf-8") as handle:
                return json.load(handle)


        def import_texture(source_root, content_root, spec):
            """Imports one PNG as a Texture2D, reusing the asset if it already exists."""
            package_path = "{0}/Textures".format(content_root)
            asset_path = "{0}/{1}".format(package_path, spec["assetName"])

            if unreal.EditorAssetLibrary.does_asset_exist(asset_path):
                texture = unreal.EditorAssetLibrary.load_asset(asset_path)
            else:
                source_file = os.path.join(source_root, spec["sourceFile"].replace("/", os.sep))
                if not os.path.isfile(source_file):
                    unreal.log_warning("Missing source image: {0}".format(source_file))
                    return None

                task = unreal.AssetImportTask()
                task.filename = source_file
                task.destination_path = package_path
                task.destination_name = spec["assetName"]
                task.automated = True
                task.replace_existing = True
                task.save = False
                unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])

                texture = unreal.EditorAssetLibrary.load_asset(asset_path)
                if texture is None:
                    unreal.log_warning("Import failed: {0}".format(source_file))
                    return None

            texture.set_editor_property("srgb", spec["srgb"])
            texture.set_editor_property(
                "compression_settings",
                COMPRESSION.get(spec["compressionSettings"], unreal.TextureCompressionSettings.TC_DEFAULT),
            )
            unreal.EditorAssetLibrary.save_loaded_asset(texture, only_if_is_dirty=True)
            return texture


        def build_material(content_root, spec, textures_by_role):
            """Creates (or replaces) a material and connects every imported map to its input."""
            package_path = "{0}/Materials".format(content_root)
            asset_path = "{0}/{1}".format(package_path, spec["materialName"])

            if unreal.EditorAssetLibrary.does_asset_exist(asset_path):
                unreal.EditorAssetLibrary.delete_asset(asset_path)

            material = unreal.AssetToolsHelpers.get_asset_tools().create_asset(
                asset_name=spec["materialName"],
                package_path=package_path,
                asset_class=unreal.Material,
                factory=unreal.MaterialFactoryNew(),
            )

            material.set_editor_property("blend_mode", BLEND_MODES.get(spec["blendMode"], unreal.BlendMode.BLEND_OPAQUE))
            material.set_editor_property("two_sided", spec["twoSided"])

            node_y = -400
            base_color_node = None

            for texture_spec in spec["textures"]:
                if not texture_spec["connectToMaterial"]:
                    continue

                role = texture_spec["baseRole"]
                texture = textures_by_role.get(texture_spec["role"])
                if texture is None or role not in MATERIAL_INPUTS:
                    continue

                sample = unreal.MaterialEditingLibrary.create_material_expression(
                    material, unreal.MaterialExpressionTextureSample, -400, node_y
                )
                sample.texture = texture
                node_y += 260

                unreal.MaterialEditingLibrary.connect_material_property(sample, "RGB", MATERIAL_INPUTS[role])
                if role == "BaseColor":
                    base_color_node = sample

            # A masked material needs the alpha of the base colour driving opacity.
            if spec["blendMode"] == "MASKED" and base_color_node is not None:
                unreal.MaterialEditingLibrary.connect_material_property(
                    base_color_node, "A", unreal.MaterialProperty.MP_OPACITY_MASK
                )

            unreal.MaterialEditingLibrary.recompile_material(material)
            unreal.EditorAssetLibrary.save_loaded_asset(material, only_if_is_dirty=True)
            return material


        def main():
            manifest = load_manifest()
            content_root = manifest["contentRoot"]
            source_root = manifest["sourceRoot"]
            materials = manifest["materials"]

            unreal.log("GameStudio: importing {0} materials into {1}".format(len(materials), content_root))

            with unreal.ScopedSlowTask(len(materials), "Importing GameStudio assets") as task:
                task.make_dialog(True)

                for spec in materials:
                    if task.should_cancel():
                        unreal.log_warning("GameStudio: import cancelled by user")
                        break
                    task.enter_progress_frame(1, spec["materialName"])

                    textures_by_role = {}
                    for texture_spec in spec["textures"]:
                        texture = import_texture(source_root, content_root, texture_spec)
                        if texture is not None:
                            textures_by_role[texture_spec["role"]] = texture

                    if textures_by_role:
                        build_material(content_root, spec, textures_by_role)
                    else:
                        unreal.log_warning("Skipped {0}: no textures imported".format(spec["materialName"]))

            unreal.log("GameStudio: import finished")


        if __name__ == "__main__":
            main()

        """";
}
