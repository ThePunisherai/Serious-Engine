namespace GameStudio.Core.Imaging;

/// <summary>Straight (non-premultiplied) 8-bit RGBA image, row-major, top-left origin.</summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Length is Width * Height * 4, ordered R,G,B,A.</summary>
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
    }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (pixels.Length != width * height * 4)
            throw new ArgumentException($"Expected {width * height * 4} bytes, got {pixels.Length}.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Stride => Width * 4;

    public int IndexOf(int x, int y) => (y * Width + x) * 4;

    public (byte R, byte G, byte B, byte A) GetPixel(int x, int y)
    {
        int i = IndexOf(x, y);
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }

    public void SetPixel(int x, int y, byte r, byte g, byte b, byte a)
    {
        int i = IndexOf(x, y);
        Pixels[i] = r;
        Pixels[i + 1] = g;
        Pixels[i + 2] = b;
        Pixels[i + 3] = a;
    }

    public RgbaImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    /// <summary>True when every alpha sample is 255, i.e. the alpha channel carries no information.</summary>
    public bool IsOpaque()
    {
        for (int i = 3; i < Pixels.Length; i += 4)
            if (Pixels[i] != 255) return false;
        return true;
    }

    /// <summary>
    /// Perceptual luminance in linear light, as a 0..1 plane. Used as the height/albedo signal
    /// that the PBR generator derives normal, roughness and AO maps from.
    /// </summary>
    public float[] ToLuminancePlane()
    {
        var plane = new float[Width * Height];
        for (int p = 0; p < plane.Length; p++)
        {
            int i = p * 4;
            float r = ColorSpace.SrgbToLinear(Pixels[i]);
            float g = ColorSpace.SrgbToLinear(Pixels[i + 1]);
            float b = ColorSpace.SrgbToLinear(Pixels[i + 2]);
            plane[p] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }
        return plane;
    }

    public static RgbaImage FromPlane(float[] plane, int width, int height, bool encodeAsSrgb)
    {
        var img = new RgbaImage(width, height);
        for (int p = 0; p < plane.Length; p++)
        {
            float v = Math.Clamp(plane[p], 0f, 1f);
            byte b = encodeAsSrgb ? ColorSpace.LinearToSrgb(v) : (byte)MathF.Round(v * 255f);
            int i = p * 4;
            img.Pixels[i] = b;
            img.Pixels[i + 1] = b;
            img.Pixels[i + 2] = b;
            img.Pixels[i + 3] = 255;
        }
        return img;
    }
}
