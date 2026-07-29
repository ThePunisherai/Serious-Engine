namespace GameStudio.Core.Imaging;

public enum ResampleFilter
{
    /// <summary>Nearest neighbour. Preserves the original pixel-art look of low-res engine textures.</summary>
    Nearest,
    Bilinear,
    /// <summary>Catmull-Rom bicubic: sharper than bilinear without Lanczos' ringing.</summary>
    Bicubic,
    /// <summary>Lanczos-3. Sharpest of the three, best default for photographic-style textures.</summary>
    Lanczos3,
}

/// <summary>
/// Separable image resampler. All weighting happens in linear light: averaging sRGB-encoded
/// bytes directly is what makes naive upscales of the original textures look muddy.
/// </summary>
public static class Resampler
{
    public static RgbaImage Resize(RgbaImage source, int width, int height, ResampleFilter filter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (width == source.Width && height == source.Height) return source.Clone();
        if (filter == ResampleFilter.Nearest) return ResizeNearest(source, width, height);

        // Colour is resampled in linear light; alpha is already linear and must not be transformed.
        float[] linear = ToLinearPlanes(source);
        float[] horizontal = ResampleAxis(linear, source.Width, source.Height, width, filter, horizontally: true);
        float[] vertical = ResampleAxis(horizontal, width, source.Height, height, filter, horizontally: false);
        return FromLinearPlanes(vertical, width, height);
    }

    public static RgbaImage Scale(RgbaImage source, float factor, ResampleFilter filter)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        int width = Math.Max(1, (int)MathF.Round(source.Width * factor));
        int height = Math.Max(1, (int)MathF.Round(source.Height * factor));
        return Resize(source, width, height, filter);
    }

    private static RgbaImage ResizeNearest(RgbaImage source, int width, int height)
    {
        var result = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
        {
            int sy = Math.Min(source.Height - 1, y * source.Height / height);
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Min(source.Width - 1, x * source.Width / width);
                source.Pixels.AsSpan(source.IndexOf(sx, sy), 4).CopyTo(result.Pixels.AsSpan(result.IndexOf(x, y), 4));
            }
        }
        return result;
    }

    private static float[] ToLinearPlanes(RgbaImage image)
    {
        var planes = new float[image.Width * image.Height * 4];
        for (int i = 0; i < planes.Length; i += 4)
        {
            planes[i] = ColorSpace.SrgbToLinear(image.Pixels[i]);
            planes[i + 1] = ColorSpace.SrgbToLinear(image.Pixels[i + 1]);
            planes[i + 2] = ColorSpace.SrgbToLinear(image.Pixels[i + 2]);
            planes[i + 3] = image.Pixels[i + 3] / 255f;
        }
        return planes;
    }

    private static RgbaImage FromLinearPlanes(float[] planes, int width, int height)
    {
        var image = new RgbaImage(width, height);
        for (int i = 0; i < planes.Length; i += 4)
        {
            image.Pixels[i] = ColorSpace.LinearToSrgb(planes[i]);
            image.Pixels[i + 1] = ColorSpace.LinearToSrgb(planes[i + 1]);
            image.Pixels[i + 2] = ColorSpace.LinearToSrgb(planes[i + 2]);
            image.Pixels[i + 3] = (byte)Math.Clamp(MathF.Round(planes[i + 3] * 255f), 0f, 255f);
        }
        return image;
    }

    private static float[] ResampleAxis(float[] source, int sourceWidth, int sourceHeight,
        int targetLength, ResampleFilter filter, bool horizontally)
    {
        int sourceLength = horizontally ? sourceWidth : sourceHeight;
        int otherLength = horizontally ? sourceHeight : sourceWidth;
        int outWidth = horizontally ? targetLength : sourceWidth;

        var weights = BuildWeights(sourceLength, targetLength, filter);
        var result = new float[(horizontally ? targetLength * sourceHeight : sourceWidth * targetLength) * 4];

        for (int other = 0; other < otherLength; other++)
        {
            for (int target = 0; target < targetLength; target++)
            {
                var contributions = weights[target];
                float r = 0, g = 0, b = 0, a = 0;

                foreach (var (index, weight) in contributions)
                {
                    int sourceIndex = horizontally
                        ? (other * sourceWidth + index) * 4
                        : (index * sourceWidth + other) * 4;
                    r += source[sourceIndex] * weight;
                    g += source[sourceIndex + 1] * weight;
                    b += source[sourceIndex + 2] * weight;
                    a += source[sourceIndex + 3] * weight;
                }

                int destination = horizontally
                    ? (other * outWidth + target) * 4
                    : (target * outWidth + other) * 4;
                result[destination] = Math.Clamp(r, 0f, 1f);
                result[destination + 1] = Math.Clamp(g, 0f, 1f);
                result[destination + 2] = Math.Clamp(b, 0f, 1f);
                result[destination + 3] = Math.Clamp(a, 0f, 1f);
            }
        }

        return result;
    }

    private static (int Index, float Weight)[][] BuildWeights(int sourceLength, int targetLength, ResampleFilter filter)
    {
        float scale = (float)targetLength / sourceLength;
        // When downscaling, widen the kernel so the filter averages every source pixel that
        // falls into a destination pixel; otherwise minification aliases badly.
        float support = FilterSupport(filter) / MathF.Min(scale, 1f);

        var weights = new (int, float)[targetLength][];
        var buffer = new List<(int, float)>();

        for (int target = 0; target < targetLength; target++)
        {
            float center = (target + 0.5f) / scale - 0.5f;
            int first = (int)MathF.Ceiling(center - support);
            int last = (int)MathF.Floor(center + support);

            buffer.Clear();
            float total = 0;
            for (int i = first; i <= last; i++)
            {
                float weight = Evaluate(filter, (i - center) * MathF.Min(scale, 1f));
                if (weight == 0f) continue;
                buffer.Add((Math.Clamp(i, 0, sourceLength - 1), weight));
                total += weight;
            }

            if (buffer.Count == 0)
            {
                weights[target] = [(Math.Clamp((int)MathF.Round(center), 0, sourceLength - 1), 1f)];
                continue;
            }

            var normalized = new (int, float)[buffer.Count];
            for (int i = 0; i < buffer.Count; i++)
                normalized[i] = (buffer[i].Item1, buffer[i].Item2 / total);
            weights[target] = normalized;
        }

        return weights;
    }

    private static float FilterSupport(ResampleFilter filter) => filter switch
    {
        ResampleFilter.Bilinear => 1f,
        ResampleFilter.Bicubic => 2f,
        ResampleFilter.Lanczos3 => 3f,
        _ => 0.5f,
    };

    private static float Evaluate(ResampleFilter filter, float x) => filter switch
    {
        ResampleFilter.Bilinear => Triangle(x),
        ResampleFilter.Bicubic => CatmullRom(x),
        ResampleFilter.Lanczos3 => Lanczos(x, 3f),
        _ => MathF.Abs(x) < 0.5f ? 1f : 0f,
    };

    private static float Triangle(float x)
    {
        x = MathF.Abs(x);
        return x < 1f ? 1f - x : 0f;
    }

    private static float CatmullRom(float x)
    {
        x = MathF.Abs(x);
        if (x < 1f) return 1.5f * x * x * x - 2.5f * x * x + 1f;
        if (x < 2f) return -0.5f * x * x * x + 2.5f * x * x - 4f * x + 2f;
        return 0f;
    }

    private static float Lanczos(float x, float lobes)
    {
        x = MathF.Abs(x);
        if (x < 1e-6f) return 1f;
        if (x >= lobes) return 0f;
        float pix = MathF.PI * x;
        return lobes * MathF.Sin(pix) * MathF.Sin(pix / lobes) / (pix * pix);
    }
}
