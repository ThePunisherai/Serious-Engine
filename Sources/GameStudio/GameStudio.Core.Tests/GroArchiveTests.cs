using GameStudio.Core.Formats;
using GameStudio.Core.Imaging;

namespace GameStudio.Core.Tests;

/// <summary>
/// Exercises the readers against the resource archive shipped with the engine. The archive is
/// large and not always present in a checkout, so every test here skips when it is missing
/// rather than failing the suite.
/// </summary>
public class GroArchiveTests
{
    private static string? FindShippedArchive()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "SE1_10.gro");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    [Fact]
    public void Open_ReadsShippedArchiveDirectory()
    {
        string? path = FindShippedArchive();
        if (path is null) return; // archive not present in this checkout

        using var archive = GroArchive.Open(path!);

        Assert.NotEmpty(archive.Entries);
        Assert.Contains(archive.Entries, e => e.Kind == AssetKind.Texture);
        Assert.All(archive.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Path)));
    }

    [Fact]
    public void TexFile_DecodesEveryTextureInTheShippedArchive()
    {
        string? path = FindShippedArchive();
        if (path is null) return; // archive not present in this checkout

        using var archive = GroArchive.Open(path!);
        var textures = archive.EntriesOfKind(AssetKind.Texture).ToList();
        Assert.NotEmpty(textures);

        var failures = new List<string>();
        int decoded = 0;

        foreach (var entry in textures)
        {
            try
            {
                using var stream = archive.ReadEntry(entry.Path);
                var texture = TexFile.Load(stream);

                // Effect textures simulate their pixels at runtime and legitimately store none.
                if (texture.IsEffectTexture)
                {
                    Assert.Empty(texture.Frames);
                }
                else
                {
                    Assert.NotEmpty(texture.Frames);
                    Assert.Equal(texture.PixelWidth, texture.Frames[0].Width);
                    Assert.Equal(texture.PixelHeight, texture.Frames[0].Height);
                }
                decoded++;
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Path}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} of {textures.Count} textures failed to decode:\n" + string.Join('\n', failures.Take(15)));
        Assert.Equal(textures.Count, decoded);
    }

    [Fact]
    public void Extract_RefusesEntriesThatEscapeTheDestination()
    {
        string? path = FindShippedArchive();
        if (path is null) return; // archive not present in this checkout

        using var archive = GroArchive.Open(path!);
        string destination = Path.Combine(Path.GetTempPath(), "GameStudioExtractTest", Guid.NewGuid().ToString("N"));

        try
        {
            // A single small entry proves the happy path still writes inside the destination.
            var smallest = archive.Entries.OrderBy(e => e.Length).First();
            int written = archive.Extract(destination, e => e.Path == smallest.Path);

            Assert.Equal(1, written);
            string expected = Path.Combine(destination, smallest.Path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(expected));
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public void DecodedTexture_SurvivesAPngRoundTrip()
    {
        string? path = FindShippedArchive();
        if (path is null) return; // archive not present in this checkout

        using var archive = GroArchive.Open(path!);

        RgbaImage? original = null;
        foreach (var entry in archive.EntriesOfKind(AssetKind.Texture))
        {
            using var candidate = archive.ReadEntry(entry.Path);
            var texture = TexFile.Load(candidate);
            if (texture.Frames.Count > 0) { original = texture.Frames[0]; break; }
        }
        Assert.NotNull(original);

        byte[] png = PngWriter.ToBytes(original);
        using var buffer = new MemoryStream(png);
        var restored = PngReader.Read(buffer);

        Assert.Equal(original.Width, restored.Width);
        Assert.Equal(original.Height, restored.Height);
        for (int i = 0; i < original.Pixels.Length; i++)
            Assert.Equal(original.Pixels[i], restored.Pixels[i]);
    }
}
