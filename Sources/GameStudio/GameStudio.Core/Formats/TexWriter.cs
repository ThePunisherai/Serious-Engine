using System.Text;
using GameStudio.Core.Imaging;

namespace GameStudio.Core.Formats;

/// <summary>
/// Writes Serious Engine 1 <c>.tex</c> files in the version 4 layout, so the original engine loads
/// them with no code change at all.
/// <para>
/// Version 4 rather than 3 on purpose: it stores frames as raw 24- or 32-bit RGB(A) at full
/// resolution, where version 3 packs a whole mip chain at 16 bits per texel. Writing version 4
/// keeps every bit the upgrade pipeline produced instead of quantising it away, and the engine
/// rebuilds the mip chain on load either way.
/// </para>
/// </summary>
public static class TexWriter
{
    private const int Version = 4;

    /// <summary>
    /// Writes a texture. <paramref name="mexWidth"/> and <paramref name="mexHeight"/> are the
    /// engine's world-space size and must be carried over unchanged from the source texture —
    /// they decide how large the texture appears on a surface, not how detailed it is.
    /// </summary>
    /// <param name="firstMipLevel">
    /// How far below the mex size the stored pixels sit: the engine derives pixel width as
    /// <c>mexWidth >> firstMipLevel</c>. Lowering it is what makes a texture sharper at unchanged
    /// world scale.
    /// </param>
    public static void Write(Stream stream, IReadOnlyList<RgbaImage> frames, TexFlags flags,
        int mexWidth, int mexHeight, int fineMipLevels, int firstMipLevel)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0) throw new ArgumentException("A texture needs at least one frame.", nameof(frames));

        int pixelWidth = mexWidth >> firstMipLevel;
        int pixelHeight = mexHeight >> firstMipLevel;
        if (pixelWidth <= 0 || pixelHeight <= 0)
            throw new ArgumentException(
                $"First mip level {firstMipLevel} collapses {mexWidth}x{mexHeight} to nothing.", nameof(firstMipLevel));

        foreach (RgbaImage frame in frames)
        {
            if (frame.Width != pixelWidth || frame.Height != pixelHeight)
                throw new ArgumentException(
                    $"Frame is {frame.Width}x{frame.Height} but the header describes {pixelWidth}x{pixelHeight}.",
                    nameof(frames));
        }

        bool hasAlpha = flags.HasFlag(TexFlags.AlphaChannel);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        WriteChunkId(writer, "TVER");
        writer.Write(Version);

        WriteChunkId(writer, "TDAT");
        writer.Write((uint)flags);
        writer.Write(mexWidth);
        writer.Write(mexHeight);
        writer.Write(fineMipLevels);
        // Version 4 omits the total mip count and the legacy frame size that version 3 carries;
        // the engine recomputes both on load.
        writer.Write(firstMipLevel);
        writer.Write(frames.Count);

        WriteChunkId(writer, "FRMS");
        foreach (RgbaImage frame in frames) WriteFrame(writer, frame, hasAlpha);
    }

    public static void Write(string path, IReadOnlyList<RgbaImage> frames, TexFlags flags,
        int mexWidth, int mexHeight, int fineMipLevels, int firstMipLevel)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using FileStream stream = File.Create(path);
        Write(stream, frames, flags, mexWidth, mexHeight, fineMipLevels, firstMipLevel);
    }

    private static void WriteFrame(BinaryWriter writer, RgbaImage frame, bool hasAlpha)
    {
        byte[] pixels = frame.Pixels;
        if (hasAlpha)
        {
            writer.Write(pixels);
            return;
        }

        // Drop the alpha byte per pixel rather than writing it, matching the 24-bit layout the
        // reader expects when the alpha flag is clear.
        var packed = new byte[frame.Width * frame.Height * 3];
        for (int source = 0, target = 0; target < packed.Length; source += 4, target += 3)
        {
            packed[target] = pixels[source];
            packed[target + 1] = pixels[source + 1];
            packed[target + 2] = pixels[source + 2];
        }
        writer.Write(packed);
    }

    private static void WriteChunkId(BinaryWriter writer, string id)
    {
        if (id.Length != 4) throw new ArgumentException("Chunk identifiers are four characters.", nameof(id));
        writer.Write(Encoding.ASCII.GetBytes(id));
    }
}
