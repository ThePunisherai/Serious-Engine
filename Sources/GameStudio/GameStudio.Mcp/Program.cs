using GameStudio.Mcp;

// GameStudio Engine MCP server. Speaks JSON-RPC over stdio, so stdout carries protocol traffic
// only — anything diagnostic has to go to stderr or it corrupts the stream.
//
//   GameStudio.Mcp [--root <directory>] [--no-heartbeat]
//
//   --root          confines every path argument to this directory (recommended)
//   --no-heartbeat  skips the liveness file the desktop app polls for its status badge

string? root = null;
bool heartbeatEnabled = true;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root" when i + 1 < args.Length:
            root = args[++i];
            break;
        case "--no-heartbeat":
            heartbeatEnabled = false;
            break;
        case "--help" or "-h":
            Console.Error.WriteLine("Usage: GameStudio.Mcp [--root <directory>] [--no-heartbeat]");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
            return 2;
    }
}

if (root is not null && !Directory.Exists(root))
{
    Console.Error.WriteLine($"Workspace root '{root}' does not exist.");
    return 2;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

await using var heartbeat = heartbeatEnabled ? new Heartbeat() : null;
var server = new McpServer(new ToolCatalog(root), Console.In, Console.Out, heartbeat);

Console.Error.WriteLine($"GameStudio Engine MCP server ready{(root is null ? "" : $" (root: {root})")}.");

try
{
    await server.RunAsync(shutdown.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C or the client closing the pipe; both are ordinary shutdowns.
}

return 0;
