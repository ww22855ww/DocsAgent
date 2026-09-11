using AgentApi.Models;
using AgentApi.Services.Classification;
using AgentApi.Services.Agent;
using AgentApi.Services.Llm;
using AgentApi.Services.Mcp;
using AgentApi.Services.Tasks;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var options = AppOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddHttpClient();
builder.Services.AddControllers();
builder.Services.AddSingleton<LlmClient>();
builder.Services.AddSingleton<DocumentClassifier>();
builder.Services.AddSingleton<McpToolClient>();
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddSingleton<ScriptedAgentRunner>();
builder.Services.AddSingleton<LlmAgentRunner>();
builder.Services.AddSingleton<TaskService>();

var app = builder.Build();

app.MapControllers();

// Liveness: does not touch dependencies.
// "/health" is used by the container healthcheck; "/api/health" is what the
// browser reaches through the frontend's nginx /api/ proxy.
var liveness = (AppOptions o) => Results.Json(new
{
    status = "ok",
    app = "Agent API",
    agentMode = o.AgentMode,
    llmProvider = o.Primary.Name,
    llmModel = o.Primary.Model,
});
app.MapGet("/health", liveness);
app.MapGet("/api/health", liveness);

// Readiness: verifies Postgres and the MCP worker are actually reachable.
app.MapGet("/api/ready", async (AppOptions o, IHttpClientFactory f, CancellationToken ct) =>
{
    var checks = new Dictionary<string, string>();

    try
    {
        await using var conn = new NpgsqlConnection(o.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM mock_suppliers", conn);
        var n = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        checks["postgres"] = $"ok ({n} mock suppliers)";
    }
    catch (Exception ex) { checks["postgres"] = $"fail: {ex.Message}"; }

    try
    {
        var http = f.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        var baseUri = new Uri(o.McpUrl);
        var healthUrl = new Uri(baseUri, "/health");
        var res = await http.GetAsync(healthUrl, ct);
        checks["mcp-worker"] = res.IsSuccessStatusCode ? "ok" : $"fail: HTTP {(int)res.StatusCode}";
    }
    catch (Exception ex) { checks["mcp-worker"] = $"fail: {ex.Message}"; }

    var healthy = checks.Values.All(v => v.StartsWith("ok"));
    return Results.Json(new { status = healthy ? "ready" : "degraded", checks },
                        statusCode: healthy ? 200 : 503);
});

app.Run();
