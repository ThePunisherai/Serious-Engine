using System.Text.Json;

namespace GameStudio.Mcp;

/// <summary>
/// Periodically rewrites a small JSON file so the GameStudio desktop app can show a live
/// online/offline badge. There is no connection for the app to inspect — MCP talks JSON-RPC over
/// stdio to whichever client launched this process — so a shared file is the simplest reliable
/// cross-process signal. The file format matches the one XFS Studio already polls.
/// </summary>
public sealed class Heartbeat : IAsyncDisposable
{
    public static string DefaultPath => Path.Combine(Path.GetTempPath(), "GameStudioMcp.heartbeat");

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private readonly string _path;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public Heartbeat(string? path = null, string mode = "none")
    {
        _path = path ?? DefaultPath;
        Mode = mode;
        _loop = RunAsync(_stop.Token);
    }

    /// <summary>Short description of what the server is bound to, shown next to the badge.</summary>
    public string Mode { get; set; }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Write();
            try { await Task.Delay(Interval, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Write()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(new
            {
                utc = DateTime.UtcNow.ToString("O"),
                mode = Mode,
                pid = Environment.ProcessId,
            }));
        }
        catch (IOException)
        {
            // A transient clash with the reading app is not worth interrupting the server for.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _loop; } catch (OperationCanceledException) { /* expected */ }
        _stop.Dispose();
        try { File.Delete(_path); } catch (IOException) { /* best effort */ }
    }
}
