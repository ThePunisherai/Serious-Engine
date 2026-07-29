using GameStudio.Core.Formats;

namespace GameStudio.App;

/// <summary>One row in the archive browser.</summary>
public sealed record EntryRow(GroEntry Entry)
{
    public string Path => Entry.Path;
    public string Name => Entry.Name;
    public string Detail => $"{Entry.Kind} · {FormatSize(Entry.Length)} · {Entry.Directory}";

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.##} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>One archive card on the overview page.</summary>
public sealed record ArchiveRow(string Path, string Name, string Summary);

/// <summary>One entry in the MCP tool list.</summary>
public sealed record McpToolRow(string Name, string Description);
