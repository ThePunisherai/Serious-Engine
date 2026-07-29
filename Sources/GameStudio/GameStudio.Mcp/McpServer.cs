using System.Text.Json;
using System.Text.Json.Nodes;

namespace GameStudio.Mcp;

/// <summary>
/// Model Context Protocol server speaking JSON-RPC 2.0 over stdio, one JSON message per line.
/// Exposes the GameStudio asset toolchain so an MCP client can inspect engine archives, decode
/// textures, run the modernization pipeline and generate an Unreal import package.
/// </summary>
public sealed class McpServer
{
    private const string DefaultProtocolVersion = "2025-06-18";

    private readonly ToolCatalog _tools;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Heartbeat? _heartbeat;

    public McpServer(ToolCatalog tools, TextReader input, TextWriter output, Heartbeat? heartbeat = null)
    {
        _tools = tools;
        _input = input;
        _output = output;
        _heartbeat = heartbeat;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await _input.ReadLineAsync(cancellationToken);
            if (line is null) break;                       // client closed stdin
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? response;
            try
            {
                response = await HandleAsync(JsonNode.Parse(line)!, cancellationToken);
            }
            catch (JsonException ex)
            {
                response = Error(null, -32700, $"Parse error: {ex.Message}");
            }
            catch (Exception ex)
            {
                response = Error(null, -32603, $"Internal error: {ex.Message}");
            }

            // Notifications produce no response.
            if (response is null) continue;

            await _output.WriteLineAsync(response.ToJsonString());
            await _output.FlushAsync(cancellationToken);
        }
    }

    private async Task<JsonNode?> HandleAsync(JsonNode request, CancellationToken cancellationToken)
    {
        string? method = request["method"]?.GetValue<string>();
        JsonNode? id = request["id"]?.DeepClone();
        JsonNode? parameters = request["params"];

        // A message without an id is a notification: act on it, answer nothing.
        if (id is null)
        {
            return null;
        }

        switch (method)
        {
            case "initialize":
                string protocolVersion = parameters?["protocolVersion"]?.GetValue<string>() ?? DefaultProtocolVersion;
                if (_heartbeat is not null)
                {
                    string client = parameters?["clientInfo"]?["name"]?.GetValue<string>() ?? "client";
                    _heartbeat.Mode = client;
                }
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = protocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "gamestudio-engine",
                        ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                    },
                    ["instructions"] = "Inspect Serious Engine .gro archives and .tex textures, "
                        + "modernize textures into PBR material sets, and generate an Unreal Engine 5 import package.",
                });

            case "ping":
                return Result(id, new JsonObject());

            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = _tools.Describe() });

            case "tools/call":
                string? name = parameters?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                    return Error(id, -32602, "tools/call requires a tool name.");

                var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
                return Result(id, await _tools.CallAsync(name, arguments, cancellationToken));

            default:
                return Error(id, -32601, $"Unknown method '{method}'.");
        }
    }

    private static JsonNode Result(JsonNode? id, JsonNode result) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonNode Error(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
