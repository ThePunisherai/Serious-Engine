using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameStudio.Core;

/// <summary>
/// The single JSON contract for every file GameStudio writes and reads back — the modernization
/// manifest, the Unreal import manifest, and the MCP tool payloads. Keeping one options instance
/// is what stops a writer and a reader from silently disagreeing about property casing.
/// </summary>
public static class GameStudioJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
