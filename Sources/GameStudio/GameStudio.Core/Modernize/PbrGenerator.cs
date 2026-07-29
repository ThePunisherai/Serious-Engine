using GameStudio.Core.Imaging;

namespace GameStudio.Core.Modernize;

public sealed record PbrOptions
{
    /// <summary>Strength of the derived surface relief. 1.0 is a neutral starting point.</summary>
    public float NormalStrength { get; init; } = 1.0f;

    /// <summary>Radius, in pixels, of the blur that separates large-scale shape from fine detail.</summary>
    public int DetailRadius { get; init; } = 4;

    /// <summary>Roughness assigned to a perfectly flat, featureless surface.</summary>
    public float BaseRoughness { get; init; } = 0.65f;

    /// <summary>How strongly local detail contrast lowers roughness (polishes the surface).</summary>
    public float RoughnessContrast { get; init; } = 0.55f;

    /// <summary>Strength of the derived cavity ambient occlusion.</summary>
    public float AmbientOcclusionStrength { get; init; } = 0.75f;

    /// <summary>Emit a flat metallic map. Off by default: guessing metalness from albedo is unreliable.</summary>
    public bool GenerateMetallic { get; init; }

    public float MetallicValue { get; init; }
}

public sealed record PbrMaps(RgbaImage BaseColor, RgbaImage Normal, RgbaImage Roughness,
    RgbaImage AmbientOcclusion, RgbaImage? Metallic, RgbaImage Height);

/// <summary>
/// Derives a plausible PBR material set from a single legacy diffuse texture.
/// <para>
/// This is an approximation, not a reconstruction: the original textures predate PBR and carry
/// no measured surface data. The height signal comes from perceptual luminance, relief comes
/// from its gradient, and roughness and occlusion come from local detail contrast and cavity
/// depth respectively. The result is a sound starting point for an artist, not a final material.
/// </para>
/// </summary>
public static class PbrGenerator
{
    public static PbrMaps Generate(RgbaImage baseColor, PbrOptions? options = null)
    {
        options ??= new PbrOptions();

        int width = baseColor.Width;
        int height = baseColor.Height;

        float[] luminance = baseColor.ToLuminancePlane();
        float[] blurred = BoxBlur(luminance, width, height, Math.Max(1, options.DetailRadius));

        var normal = BuildNormalMap(luminance, width, height, options.NormalStrength);
        var roughness = BuildRoughnessMap(luminance, blurred, width, height, options);
        var occlusion = BuildOcclusionMap(luminance, blurred, width, height, options.AmbientOcclusionStrength);
        var heightMap = RgbaImage.FromPlane(luminance, width, height, encodeAsSrgb: false);

        RgbaImage? metallic = null;
        if (options.GenerateMetallic)
        {
            var flat = new float[width * height];
            Array.Fill(flat, Math.Clamp(options.MetallicValue, 0f, 1f));
            metallic = RgbaImage.FromPlane(flat, width, height, encodeAsSrgb: false);
        }

        return new PbrMaps(baseColor, normal, roughness, occlusion, metallic, heightMap);
    }

    /// <summary>
    /// Sobel gradient of the height field, encoded as a tangent-space normal map in the
    /// OpenGL/Unreal +Y-up convention. Sampling wraps, because engine textures tile.
    /// </summary>
    private static RgbaImage BuildNormalMap(float[] height, int width, int imageHeight, float strength)
    {
        var normal = new RgbaImage(width, imageHeight);

        for (int y = 0; y < imageHeight; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float tl = Sample(height, width, imageHeight, x - 1, y - 1);
                float t = Sample(height, width, imageHeight, x, y - 1);
                float tr = Sample(height, width, imageHeight, x + 1, y - 1);
                float l = Sample(height, width, imageHeight, x - 1, y);
                float r = Sample(height, width, imageHeight, x + 1, y);
                float bl = Sample(height, width, imageHeight, x - 1, y + 1);
                float b = Sample(height, width, imageHeight, x, y + 1);
                float br = Sample(height, width, imageHeight, x + 1, y + 1);

                float dx = (tr + 2f * r + br) - (tl + 2f * l + bl);
                float dy = (bl + 2f * b + br) - (tl + 2f * t + tr);

                // Scale by resolution so relief reads the same after an upscale.
                float scale = strength * width / 256f;
                float nx = -dx * scale;
                float ny = -dy * scale;
                const float nz = 1f;

                float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                nx /= length; ny /= length;
                float nzn = nz / length;

                normal.SetPixel(x, y,
                    (byte)Math.Clamp(MathF.Round((nx * 0.5f + 0.5f) * 255f), 0f, 255f),
                    (byte)Math.Clamp(MathF.Round((ny * 0.5f + 0.5f) * 255f), 0f, 255f),
                    (byte)Math.Clamp(MathF.Round((nzn * 0.5f + 0.5f) * 255f), 0f, 255f),
                    255);
            }
        }

        return normal;
    }

    /// <summary>
    /// Fine detail contrast stands in for micro-surface variation: busy areas read as worn or
    /// textured (rougher stays high), flat areas read as smoother.
    /// </summary>
    private static RgbaImage BuildRoughnessMap(float[] luminance, float[] blurred, int width, int height, PbrOptions options)
    {
        var plane = new float[luminance.Length];
        float maxDetail = 1e-6f;

        for (int i = 0; i < luminance.Length; i++)
        {
            plane[i] = MathF.Abs(luminance[i] - blurred[i]);
            if (plane[i] > maxDetail) maxDetail = plane[i];
        }

        for (int i = 0; i < plane.Length; i++)
        {
            float detail = plane[i] / maxDetail;
            // Bright, flat regions polish toward smooth; dark, busy regions stay rough.
            float brightnessRelief = luminance[i] * 0.25f;
            plane[i] = Math.Clamp(options.BaseRoughness + detail * options.RoughnessContrast - brightnessRelief, 0.04f, 1f);
        }

        return RgbaImage.FromPlane(plane, width, height, encodeAsSrgb: false);
    }

    /// <summary>Cavity occlusion: how far below its local neighbourhood a texel sits.</summary>
    private static RgbaImage BuildOcclusionMap(float[] luminance, float[] blurred, int width, int height, float strength)
    {
        var plane = new float[luminance.Length];
        for (int i = 0; i < plane.Length; i++)
        {
            float cavity = MathF.Max(0f, blurred[i] - luminance[i]);
            plane[i] = Math.Clamp(1f - cavity * strength * 4f, 0f, 1f);
        }
        return RgbaImage.FromPlane(plane, width, height, encodeAsSrgb: false);
    }

    /// <summary>Separable box blur run twice, which approximates a Gaussian closely enough here.</summary>
    private static float[] BoxBlur(float[] source, int width, int height, int radius)
    {
        float[] pass = BlurAxis(source, width, height, radius, horizontal: true);
        pass = BlurAxis(pass, width, height, radius, horizontal: false);
        pass = BlurAxis(pass, width, height, radius, horizontal: true);
        return BlurAxis(pass, width, height, radius, horizontal: false);
    }

    private static float[] BlurAxis(float[] source, int width, int height, int radius, bool horizontal)
    {
        var result = new float[source.Length];
        int outer = horizontal ? height : width;
        int inner = horizontal ? width : height;
        float divisor = radius * 2 + 1;

        for (int o = 0; o < outer; o++)
        {
            for (int i = 0; i < inner; i++)
            {
                float sum = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int s = Wrap(i + k, inner);
                    sum += horizontal ? source[o * width + s] : source[s * width + o];
                }
                if (horizontal) result[o * width + i] = sum / divisor;
                else result[i * width + o] = sum / divisor;
            }
        }

        return result;
    }

    private static float Sample(float[] plane, int width, int height, int x, int y) =>
        plane[Wrap(y, height) * width + Wrap(x, width)];

    private static int Wrap(int value, int length)
    {
        value %= length;
        return value < 0 ? value + length : value;
    }
}
