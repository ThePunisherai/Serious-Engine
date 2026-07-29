namespace GameStudio.Core.Imaging;

/// <summary>
/// sRGB transfer-function helpers. Resampling and lighting-derived maps have to happen in
/// linear light — averaging sRGB-encoded bytes directly darkens midtones noticeably, which is
/// the single most common artefact when naively upscaling the original engine's textures.
/// </summary>
public static class ColorSpace
{
    private static readonly float[] ToLinearTable = BuildToLinearTable();

    private static float[] BuildToLinearTable()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255f;
            table[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        return table;
    }

    public static float SrgbToLinear(byte value) => ToLinearTable[value];

    public static byte LinearToSrgb(float linear)
    {
        linear = Math.Clamp(linear, 0f, 1f);
        float encoded = linear <= 0.0031308f
            ? linear * 12.92f
            : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(encoded * 255f), 0f, 255f);
    }
}
