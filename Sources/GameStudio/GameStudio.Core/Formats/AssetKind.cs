namespace GameStudio.Core.Formats;

public enum AssetKind
{
    Unknown,
    Texture,
    Model,
    SkeletalModel,
    Animation,
    World,
    Sound,
    Music,
    Script,
    EntityClass,
    Text,
    Shader,
}

public static class AssetKinds
{
    // Extension map taken from the file types the engine's own browsers register; see
    // Sources/WorldEditor and Sources/Engine/Base/FileName.h for the authoritative list.
    private static readonly Dictionary<string, AssetKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".tex"] = AssetKind.Texture,
        [".mdl"] = AssetKind.Model,
        [".bm"] = AssetKind.Model,
        [".smc"] = AssetKind.SkeletalModel,
        [".bs"] = AssetKind.SkeletalModel,
        [".am"] = AssetKind.Animation,
        [".aal"] = AssetKind.Animation,
        [".ba"] = AssetKind.Animation,
        [".wld"] = AssetKind.World,
        [".vis"] = AssetKind.World,
        [".wav"] = AssetKind.Sound,
        [".ogg"] = AssetKind.Music,
        [".mp3"] = AssetKind.Music,
        [".ini"] = AssetKind.Script,
        [".scr"] = AssetKind.Script,
        [".cfg"] = AssetKind.Script,
        [".es"] = AssetKind.EntityClass,
        [".ecl"] = AssetKind.EntityClass,
        [".txt"] = AssetKind.Text,
        [".des"] = AssetKind.Text,
        [".vsh"] = AssetKind.Shader,
        [".psh"] = AssetKind.Shader,
    };

    public static AssetKind FromExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return AssetKind.Unknown;
        return ByExtension.TryGetValue(extension, out var kind) ? kind : AssetKind.Unknown;
    }

    public static AssetKind FromPath(string path) => FromExtension(Path.GetExtension(path));

    /// <summary>Kinds the modernization pipeline can currently rewrite.</summary>
    public static bool IsModernizable(AssetKind kind) => kind is AssetKind.Texture;

    /// <summary>Kinds the Unreal exporter emits something for.</summary>
    public static bool IsExportable(AssetKind kind) =>
        kind is AssetKind.Texture or AssetKind.Model or AssetKind.SkeletalModel or AssetKind.World;
}
