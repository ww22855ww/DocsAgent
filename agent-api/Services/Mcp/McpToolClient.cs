using System.Text.Json;
using System.Text.Json.Nodes;
using AgentApi.Models;
using ModelContextProtocol.Client;

namespace AgentApi.Services.Mcp;

/// <summary>
/// Talks to mcp-worker over Streamable HTTP.
///
/// The tool list is discovered rather than hard-coded: whatever the worker
/// registers is what the agent can call, so adding a tool there needs no change
/// here. The connection is created lazily and shared.
/// </summary>
public sealed class McpToolClient(AppOptions options, ILogger<McpToolClient> log) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private IList<McpClientTool>? _tools;

    private async Task<McpClient> ConnectAsync(CancellationToken ct)
    {
        if (_client is not null) return _client;

        await _gate.WaitAsync(ct);
        try
        {
            if (_client is not null) return _client;

            log.LogInformation("Connecting to MCP worker at {Url}", options.McpUrl);
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(options.McpUrl),
                Name = "mcp-worker",
            });

            _client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            _tools = await _client.ListToolsAsync(cancellationToken: ct);
            log.LogInformation("MCP worker exposes {Count} tools: {Names}",
                _tools.Count, string.Join(", ", _tools.Select(t => t.Name)));

            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IList<McpClientTool>> ListToolsAsync(CancellationToken ct = default)
    {
        await ConnectAsync(ct);
        return _tools ?? [];
    }

    /// <summary>Call one tool and return its result as JSON.</summary>
    public async Task<JsonNode> CallAsync(string name, JsonObject arguments, CancellationToken ct = default)
    {
        var client = await ConnectAsync(ct);

        var args = arguments.ToDictionary(
            kv => kv.Key,
            kv => (object?)(kv.Value?.DeepClone()));

        var result = await client.CallToolAsync(name, args, cancellationToken: ct);

        // Tools return structured content; fall back to parsing the text block
        // for servers that only populate that.
        if (result.StructuredContent is not null)
            return JsonNode.Parse(result.StructuredContent.Value.GetRawText()) ?? new JsonObject();

        var text = string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>()
                                              .Select(c => c.Text));
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject { ["status"] = result.IsError == true ? "error" : "ok" };

        try
        {
            return JsonNode.Parse(text) ?? new JsonObject { ["text"] = text };
        }
        catch (JsonException)
        {
            return new JsonObject { ["text"] = text };
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        _gate.Dispose();
    }
}
