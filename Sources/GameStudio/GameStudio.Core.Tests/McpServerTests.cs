using System.Text.Json;
using System.Text.Json.Nodes;
using GameStudio.Mcp;

namespace GameStudio.Core.Tests;

public class McpServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameStudioMcpTests", Guid.NewGuid().ToString("N"));

    public McpServerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Drives the server over a pair of in-memory pipes, one JSON message per line.</summary>
    private async Task<List<JsonNode>> ExchangeAsync(params string[] requests)
    {
        using var input = new StringReader(string.Join('\n', requests) + '\n');
        using var output = new StringWriter();

        // Heartbeat is off: tests must not touch the shared liveness file the desktop app polls.
        await new McpServer(new ToolCatalog(_root), input, output).RunAsync();

        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonNode.Parse(line)!)
            .ToList();
    }

    private static string ToolCall(int id, string name, object arguments) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new { name, arguments },
        });

    private static string TextOf(JsonNode response) =>
        response["result"]!["content"]![0]!["text"]!.GetValue<string>();

    private static bool IsErrorResult(JsonNode response) =>
        response["result"]!["isError"]!.GetValue<bool>();

    [Fact]
    public async Task Initialize_EchoesTheClientProtocolVersionAndAdvertisesTools()
    {
        var responses = await ExchangeAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");

        var result = Assert.Single(responses)["result"]!;
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.Equal("gamestudio-engine", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task Notifications_ProduceNoResponse()
    {
        var responses = await ExchangeAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":7,"method":"ping"}""");

        var only = Assert.Single(responses);
        Assert.Equal(7, only["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task ToolsList_DescribesEveryToolWithAnInputSchema()
    {
        var responses = await ExchangeAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var tools = Assert.Single(responses)["result"]!["tools"]!.AsArray();

        Assert.Equal(
            ["engine_overview", "list_archive", "texture_info", "export_texture", "modernize_textures", "export_unreal"],
            tools.Select(t => t!["name"]!.GetValue<string>()));

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool!["description"]!.GetValue<string>()));
            var schema = tool["inputSchema"]!;
            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.NotEmpty(schema["required"]!.AsArray());
        }
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var responses = await ExchangeAsync("""{"jsonrpc":"2.0","id":1,"method":"does/not/exist"}""");
        Assert.Equal(-32601, Assert.Single(responses)["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task MalformedLine_ReturnsParseErrorWithoutKillingTheServer()
    {
        var responses = await ExchangeAsync(
            "{ this is not json",
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""");

        Assert.Equal(2, responses.Count);
        Assert.Equal(-32700, responses[0]["error"]!["code"]!.GetValue<int>());
        Assert.NotNull(responses[1]["result"]);
    }

    [Fact]
    public async Task UnknownTool_IsReportedAsAToolErrorRatherThanAProtocolError()
    {
        var responses = await ExchangeAsync(ToolCall(1, "not_a_tool", new { }));
        var response = Assert.Single(responses);

        Assert.Null(response["error"]);
        Assert.True(IsErrorResult(response));
        Assert.Contains("Unknown tool", TextOf(response));
    }

    [Fact]
    public async Task MissingRequiredArgument_IsReportedAsAToolError()
    {
        var responses = await ExchangeAsync(ToolCall(1, "texture_info", new { }));
        var response = Assert.Single(responses);

        Assert.True(IsErrorResult(response));
        Assert.Contains("texture", TextOf(response));
    }

    [Fact]
    public async Task PathsOutsideTheWorkspaceRootAreRefused()
    {
        string outside = Path.Combine(Path.GetTempPath(), "definitely-not-in-the-workspace.tex");
        var responses = await ExchangeAsync(ToolCall(1, "texture_info", new { texture = outside }));
        var response = Assert.Single(responses);

        Assert.True(IsErrorResult(response));
        Assert.Contains("outside the workspace root", TextOf(response));
    }

    [Fact]
    public async Task TextureInfo_ReportsFormatDetailsForARealTexture()
    {
        string texturePath = Path.Combine(_root, "sample.tex");
        using (var texture = TexFileTests.BuildVersion4Texture(width: 16, height: 8, hasAlpha: true, frames: 2))
        using (var file = File.Create(texturePath))
            texture.CopyTo(file);

        var responses = await ExchangeAsync(ToolCall(1, "texture_info", new { texture = texturePath }));
        var response = Assert.Single(responses);
        Assert.False(IsErrorResult(response));

        using var info = JsonDocument.Parse(TextOf(response));
        Assert.Equal(4, info.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(16, info.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(8, info.RootElement.GetProperty("height").GetInt32());
        Assert.True(info.RootElement.GetProperty("hasAlpha").GetBoolean());
        Assert.Equal(2, info.RootElement.GetProperty("decodedFrames").GetInt32());
    }

    [Fact]
    public async Task ModernizeThenExportUnreal_ProducesAnImportPackage()
    {
        string source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        using (var texture = TexFileTests.BuildVersion4Texture(width: 8, height: 8, hasAlpha: false, frames: 1))
        using (var file = File.Create(Path.Combine(source, "brick.tex")))
            texture.CopyTo(file);

        string modern = Path.Combine(_root, "modern");
        string unreal = Path.Combine(_root, "unreal");

        var responses = await ExchangeAsync(
            ToolCall(1, "modernize_textures", new { source, output = modern, scale = 2 }),
            ToolCall(2, "export_unreal", new { modernizedDirectory = modern, output = unreal }));

        Assert.All(responses, r => Assert.False(IsErrorResult(r)));

        using var modernized = JsonDocument.Parse(TextOf(responses[0]));
        Assert.Equal(1, modernized.RootElement.GetProperty("converted").GetInt32());
        Assert.Equal(0, modernized.RootElement.GetProperty("failed").GetInt32());

        using var exported = JsonDocument.Parse(TextOf(responses[1]));
        Assert.Equal(1, exported.RootElement.GetProperty("materials").GetInt32());
        Assert.True(File.Exists(exported.RootElement.GetProperty("script").GetString()!));
    }

    [Fact]
    public async Task ExportUnreal_ExplainsItselfWhenTheManifestIsMissing()
    {
        var responses = await ExchangeAsync(
            ToolCall(1, "export_unreal", new { modernizedDirectory = _root, output = Path.Combine(_root, "out") }));

        var response = Assert.Single(responses);
        Assert.True(IsErrorResult(response));
        Assert.Contains("modernize_textures", TextOf(response));
    }
}
