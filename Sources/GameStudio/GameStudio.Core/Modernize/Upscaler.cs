using System.Diagnostics;
using GameStudio.Core.Imaging;

namespace GameStudio.Core.Modernize;

public enum UpscaleBackend
{
    /// <summary>Built-in sRGB-correct Lanczos/bicubic resampling. Always available, no setup.</summary>
    BuiltIn,
    /// <summary>
    /// An external neural upscaler CLI such as realesrgan-ncnn-vulkan, invoked per image.
    /// Produces far better results on the low-resolution originals, but has to be installed
    /// separately — the pipeline falls back to <see cref="BuiltIn"/> when it is unavailable.
    /// </summary>
    External,
}

public sealed record UpscaleSettings
{
    public UpscaleBackend Backend { get; init; } = UpscaleBackend.BuiltIn;
    public int Factor { get; init; } = 4;
    public ResampleFilter Filter { get; init; } = ResampleFilter.Lanczos3;

    /// <summary>Path to the external upscaler executable. Required when <see cref="Backend"/> is External.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Argument template for the external tool. <c>{input}</c>, <c>{output}</c> and <c>{scale}</c>
    /// are substituted. The default matches realesrgan-ncnn-vulkan's command line.
    /// </summary>
    public string ArgumentTemplate { get; init; } = "-i \"{input}\" -o \"{output}\" -s {scale} -n realesrgan-x4plus";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Cap on output edge length, so a 4x pass on an already-large texture stays sane.</summary>
    public int MaxDimension { get; init; } = 4096;
}

public sealed record UpscaleResult(RgbaImage Image, UpscaleBackend BackendUsed, string? Note);

public static class Upscaler
{
    public static UpscaleResult Upscale(RgbaImage source, UpscaleSettings settings, CancellationToken cancellationToken = default)
    {
        int factor = Math.Clamp(settings.Factor, 1, 8);
        if (factor == 1) return new UpscaleResult(source.Clone(), UpscaleBackend.BuiltIn, "Scale factor 1, image copied unchanged.");

        int targetWidth = source.Width * factor;
        int targetHeight = source.Height * factor;

        if (Math.Max(targetWidth, targetHeight) > settings.MaxDimension)
        {
            float clamp = (float)settings.MaxDimension / Math.Max(targetWidth, targetHeight);
            targetWidth = Math.Max(1, (int)(targetWidth * clamp));
            targetHeight = Math.Max(1, (int)(targetHeight * clamp));
        }

        if (settings.Backend == UpscaleBackend.External)
        {
            if (TryUpscaleExternally(source, settings, factor, cancellationToken, out var external, out string? failure))
            {
                var fitted = external!.Width == targetWidth && external.Height == targetHeight
                    ? external
                    : Resampler.Resize(external, targetWidth, targetHeight, settings.Filter);
                return new UpscaleResult(fitted, UpscaleBackend.External, null);
            }
            return new UpscaleResult(Resampler.Resize(source, targetWidth, targetHeight, settings.Filter),
                UpscaleBackend.BuiltIn, $"External upscaler unavailable, used built-in resampling instead: {failure}");
        }

        return new UpscaleResult(Resampler.Resize(source, targetWidth, targetHeight, settings.Filter), UpscaleBackend.BuiltIn, null);
    }

    private static bool TryUpscaleExternally(RgbaImage source, UpscaleSettings settings, int factor,
        CancellationToken cancellationToken, out RgbaImage? result, out string? failure)
    {
        result = null;
        failure = null;

        if (string.IsNullOrWhiteSpace(settings.ExecutablePath) || !File.Exists(settings.ExecutablePath))
        {
            failure = "executable not configured or not found";
            return false;
        }

        string workDirectory = Path.Combine(Path.GetTempPath(), "GameStudioUpscale", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        string input = Path.Combine(workDirectory, "input.png");
        string output = Path.Combine(workDirectory, "output.png");

        try
        {
            PngWriter.Save(source, input);

            string arguments = settings.ArgumentTemplate
                .Replace("{input}", input)
                .Replace("{output}", output)
                .Replace("{scale}", factor.ToString());

            var startInfo = new ProcessStartInfo(settings.ExecutablePath)
            {
                Arguments = arguments,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDirectory,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                failure = "process could not be started";
                return false;
            }

            if (!process.WaitForExit((int)settings.Timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                failure = $"timed out after {settings.Timeout.TotalSeconds:0}s";
                return false;
            }

            if (process.ExitCode != 0)
            {
                failure = $"exit code {process.ExitCode}";
                return false;
            }

            if (!File.Exists(output))
            {
                failure = "no output image was produced";
                return false;
            }

            result = PngReader.Load(output);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            failure = ex.Message;
            return false;
        }
        finally
        {
            try { Directory.Delete(workDirectory, recursive: true); } catch { /* best effort */ }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
