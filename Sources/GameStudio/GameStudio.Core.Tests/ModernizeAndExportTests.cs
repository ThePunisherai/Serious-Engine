using System.Text.Json;
using GameStudio.Core.Imaging;
using GameStudio.Core.Modernize;
using GameStudio.Core.Unreal;

namespace GameStudio.Core.Tests;

public class ModernizeAndExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameStudioTests", Guid.NewGuid().ToString("N"));

    public ModernizeAndExportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string WriteSourceTexture(string relativePath, int width, int height, bool hasAlpha)
    {
        string path = Path.Combine(_root, "source", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var texture = TexFileTests.BuildVersion4Texture(width, height, hasAlpha, frames: 1);
        using var file = File.Create(path);
        texture.CopyTo(file);
        return path;
    }

    [Fact]
    public void Pipeline_UpscalesAndGeneratesTheFullMapSet()
    {
        WriteSourceTexture("stone.tex", 16, 16, hasAlpha: false);
        string output = Path.Combine(_root, "modern");

        var report = new ModernizePipeline(new ModernizeSettings
        {
            Upscale = new UpscaleSettings { Factor = 4, Filter = ResampleFilter.Lanczos3 },
            SkipUpToDate = false,
        }).RunOnDirectory(Path.Combine(_root, "source"), output);

        var texture = Assert.Single(report.Textures);
        Assert.Empty(report.Failures);
        Assert.Equal(16, texture.SourceWidth);
        Assert.Equal(64, texture.OutputWidth);
        Assert.Equal(64, texture.OutputHeight);

        Assert.Equal(
            new[] { "AmbientOcclusion", "BaseColor", "Normal", "Roughness" },
            texture.Maps.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (string relative in texture.Maps.Values)
            Assert.True(File.Exists(Path.Combine(output, relative)), $"missing {relative}");

        Assert.True(File.Exists(Path.Combine(output, "materials.json")));
    }

    [Fact]
    public void Pipeline_ReportsProgressAndSkipsUpToDateAssetsOnRerun()
    {
        WriteSourceTexture("a.tex", 8, 8, hasAlpha: false);
        WriteSourceTexture("b.tex", 8, 8, hasAlpha: false);
        string source = Path.Combine(_root, "source");
        string output = Path.Combine(_root, "modern");
        var settings = new ModernizeSettings { Upscale = new UpscaleSettings { Factor = 2 }, SkipUpToDate = true };

        var seen = new List<string>();
        var first = new ModernizePipeline(settings).RunOnDirectory(source, output,
            new Progress<ModernizeProgress>(p => seen.Add(p.CurrentAsset)));

        Assert.Equal(2, first.Textures.Count);
        Assert.Empty(first.Skipped);

        var second = new ModernizePipeline(settings).RunOnDirectory(source, output);
        Assert.Empty(second.Textures);
        Assert.Equal(2, second.Skipped.Count);
    }

    [Fact]
    public void Pipeline_MarksAlphaTexturesSoTheExporterCanMaskThem()
    {
        WriteSourceTexture("leaf.tex", 8, 8, hasAlpha: true);
        var report = new ModernizePipeline(new ModernizeSettings { SkipUpToDate = false })
            .RunOnDirectory(Path.Combine(_root, "source"), Path.Combine(_root, "modern"));

        Assert.True(Assert.Single(report.Textures).HasAlpha);
    }

    [Fact]
    public void UnrealExport_WritesARunnableScriptAndCamelCaseManifest()
    {
        WriteSourceTexture("wall.tex", 8, 8, hasAlpha: true);
        var report = new ModernizePipeline(new ModernizeSettings { SkipUpToDate = false })
            .RunOnDirectory(Path.Combine(_root, "source"), Path.Combine(_root, "modern"));

        string exportDirectory = Path.Combine(_root, "unreal");
        var result = UnrealExporter.Export(report, exportDirectory, new UnrealExportSettings { ContentRoot = "/Game/Test" });

        Assert.True(File.Exists(result.ScriptPath));
        Assert.True(File.Exists(result.ManifestPath));
        Assert.Equal(1, result.MaterialCount);

        string script = File.ReadAllText(result.ScriptPath);
        Assert.StartsWith("\"\"\"", script);
        Assert.Contains("import unreal", script);

        using var manifest = JsonDocument.Parse(File.ReadAllText(result.ManifestPath));
        var root = manifest.RootElement;
        Assert.Equal("/Game/Test", root.GetProperty("contentRoot").GetString());

        var material = root.GetProperty("materials")[0];
        Assert.Equal("M_wall", material.GetProperty("materialName").GetString());
        Assert.Equal("MASKED", material.GetProperty("blendMode").GetString());
        Assert.True(material.GetProperty("twoSided").GetBoolean());

        var textures = material.GetProperty("textures").EnumerateArray().ToList();
        var baseColor = textures.Single(t => t.GetProperty("role").GetString() == "BaseColor");
        Assert.True(baseColor.GetProperty("srgb").GetBoolean());
        Assert.Equal("TC_DEFAULT", baseColor.GetProperty("compressionSettings").GetString());

        // Derived maps carry linear data and must never be flagged sRGB.
        var normal = textures.Single(t => t.GetProperty("role").GetString() == "Normal");
        Assert.False(normal.GetProperty("srgb").GetBoolean());
        Assert.Equal("TC_NORMALMAP", normal.GetProperty("compressionSettings").GetString());

        var roughness = textures.Single(t => t.GetProperty("role").GetString() == "Roughness");
        Assert.False(roughness.GetProperty("srgb").GetBoolean());
        Assert.Equal("TC_MASKS", roughness.GetProperty("compressionSettings").GetString());
    }

    [Theory]
    [InlineData("Fire 01", "Fire_01")]
    [InlineData("wall-brick.02", "wall_brick_02")]
    [InlineData("3Dgrid", "T_3Dgrid")]
    public void SanitizeName_ProducesValidUnrealIdentifiers(string input, string expected) =>
        Assert.Equal(expected, ModernizePipeline.SanitizeName(input));

    [Fact]
    public void Resampler_PreservesMidtonesByWorkingInLinearLight()
    {
        // Half black, half white. A correct linear-light average is mid-grey around 188 in sRGB;
        // averaging the encoded bytes instead would land near 128.
        var source = new RgbaImage(2, 1);
        source.SetPixel(0, 0, 0, 0, 0, 255);
        source.SetPixel(1, 0, 255, 255, 255, 255);

        var result = Resampler.Resize(source, 1, 1, ResampleFilter.Bilinear);

        var (r, _, _, a) = result.GetPixel(0, 0);
        Assert.InRange(r, 180, 195);
        Assert.Equal(255, a);
    }

    [Fact]
    public void PbrGenerator_ProducesAFlatNormalMapForAFeaturelessTexture()
    {
        var flat = new RgbaImage(16, 16);
        Array.Fill(flat.Pixels, (byte)128);
        for (int i = 3; i < flat.Pixels.Length; i += 4) flat.Pixels[i] = 255;

        var maps = PbrGenerator.Generate(flat);

        var (r, g, b, _) = maps.Normal.GetPixel(8, 8);
        Assert.InRange(r, 126, 130);
        Assert.InRange(g, 126, 130);
        Assert.InRange(b, 250, 255);
    }

    [Fact]
    public void PbrGenerator_EmitsReliefWhereTheTextureHasContrast()
    {
        var checkerboard = new RgbaImage(16, 16);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                byte v = (byte)(((x / 4 + y / 4) % 2 == 0) ? 20 : 235);
                checkerboard.SetPixel(x, y, v, v, v, 255);
            }

        var maps = PbrGenerator.Generate(checkerboard, new PbrOptions { NormalStrength = 2f });

        bool hasRelief = false;
        for (int i = 0; i < maps.Normal.Pixels.Length && !hasRelief; i += 4)
            if (Math.Abs(maps.Normal.Pixels[i] - 128) > 12) hasRelief = true;

        Assert.True(hasRelief, "expected the generated normal map to deviate from flat");
    }
}
