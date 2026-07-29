using System.IO.Compression;

namespace GameStudio.Core.Formats;

public sealed record GroEntry(string Path, long Length, long CompressedLength, AssetKind Kind)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? string.Empty;
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}

/// <summary>
/// Reader for Serious Engine <c>.gro</c> resource archives. These are plain zip containers —
/// see UNZIPAddArchive in Sources/Engine/Base/Unzip.cpp — so the framework's zip support reads
/// them directly, including the stored (uncompressed) entries the shipped archives mostly use.
/// </summary>
public sealed class GroArchive : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _byPath;

    private GroArchive(string path, ZipArchive zip)
    {
        ArchivePath = path;
        _zip = zip;
        _byPath = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        var entries = new List<GroEntry>();
        foreach (var entry in zip.Entries)
        {
            // Directory markers have an empty name and no payload.
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            string normalized = entry.FullName.Replace('\\', '/');
            _byPath[normalized] = entry;
            entries.Add(new GroEntry(normalized, entry.Length, entry.CompressedLength,
                AssetKinds.FromExtension(System.IO.Path.GetExtension(normalized))));
        }
        Entries = entries;
    }

    public string ArchivePath { get; }
    public IReadOnlyList<GroEntry> Entries { get; }
    public long TotalUncompressedBytes => Entries.Sum(e => e.Length);

    public static GroArchive Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Archive not found.", path);
        return new GroArchive(path, ZipFile.OpenRead(path));
    }

    public bool Contains(string entryPath) => _byPath.ContainsKey(entryPath.Replace('\\', '/'));

    public Stream OpenEntry(string entryPath)
    {
        string normalized = entryPath.Replace('\\', '/');
        if (!_byPath.TryGetValue(normalized, out var entry))
            throw new FileNotFoundException($"'{entryPath}' is not present in {System.IO.Path.GetFileName(ArchivePath)}.");
        return entry.Open();
    }

    /// <summary>Reads an entry fully into memory, yielding a seekable stream the format readers need.</summary>
    public MemoryStream ReadEntry(string entryPath)
    {
        using var source = OpenEntry(entryPath);
        var buffer = new MemoryStream();
        source.CopyTo(buffer);
        buffer.Position = 0;
        return buffer;
    }

    public IEnumerable<GroEntry> EntriesOfKind(AssetKind kind) => Entries.Where(e => e.Kind == kind);

    /// <summary>
    /// Extracts entries into <paramref name="destinationDirectory"/>, refusing any path that
    /// would escape it (zip-slip) rather than trusting the archive's own path strings.
    /// </summary>
    public int Extract(string destinationDirectory, Func<GroEntry, bool>? filter = null,
        IProgress<ExtractProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string root = System.IO.Path.GetFullPath(destinationDirectory);
        System.IO.Directory.CreateDirectory(root);

        var selected = filter is null ? Entries : Entries.Where(filter).ToList();
        int written = 0;

        foreach (var entry in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, entry.Path));
            if (!target.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) && target != root)
                throw new InvalidDataException($"Archive entry '{entry.Path}' escapes the destination directory.");

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            using (var source = OpenEntry(entry.Path))
            using (var destination = File.Create(target))
                source.CopyTo(destination);

            written++;
            progress?.Report(new ExtractProgress(written, selected.Count, entry.Path));
        }

        return written;
    }

    public void Dispose() => _zip.Dispose();
}

public readonly record struct ExtractProgress(int Completed, int Total, string CurrentPath)
{
    public double Fraction => Total == 0 ? 1d : (double)Completed / Total;
}
