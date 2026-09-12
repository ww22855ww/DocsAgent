using System.Diagnostics;
using System.Net.Http.Headers;
using AgentApi.Models;
using AgentApi.Services.Tasks;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace AgentApi.Controllers;

/// <summary>
/// Pre-flight check for the demo.
///
/// Every dependency the run touches is probed in parallel with a short timeout,
/// so the presenter can see at a glance that the stack is ready before standing
/// up. Reachability only: nothing here consumes LLM tokens or sends mail.
/// </summary>
[ApiController]
[Route("api/health")]
public sealed class HealthController(
    AppOptions options,
    IHttpClientFactory httpFactory,
    IConfiguration config,
    TaskService tasks) : ControllerBase
{
    private static readonly TimeSpan Probe = TimeSpan.FromSeconds(8);

    public sealed record ServiceStatus(string Key, string Name, string Detail, bool Ok, int Ms, bool Required);

    [HttpGet("services")]
    public async Task<IActionResult> Services(CancellationToken ct)
    {
        var checks = await Task.WhenAll(
            Postgres(ct),
            Http("mcp-worker", "MCP Worker", McpHealthUrl(), ct),
            Http("portal", "Mock Portal", "http://mock-portal:8080/health", ct),
            Llm(ct),
            Reachable("dbquery", "SQL 查詢 API", config["DBQUERY_BASE_URL"] + config["DBQUERY_PATH"], ct),
            Http("mail", "Mail API", (config["MAIL_API_BASE_URL"] ?? "") + "/health", ct));

        var required = checks.Where(c => c.Required).ToList();
        var ready = required.All(c => c.Ok);

        return Ok(new
        {
            ready,
            checkedAt = DateTimeOffset.Now,
            mode = new
            {
                agent = options.AgentMode,
                llmProvider = options.Primary.Name,
                llmModel = options.Primary.Model,
                dbquery = config["DBQUERY_MODE"] ?? "mock",
                mailEnabled = (config["MAIL_ENABLED"] ?? "false").Trim().ToLowerInvariant() is "true" or "1",
                busy = tasks.IsBusy,
            },
            services = checks,
        });
    }

    private string McpHealthUrl()
    {
        var baseUri = new Uri(options.McpUrl);
        return new Uri(baseUri, "/health").ToString();
    }

    private async Task<ServiceStatus> Postgres(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new NpgsqlConnection(options.PostgresConnectionString);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Probe);
            await conn.OpenAsync(cts.Token);

            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM mock_suppliers WHERE enabled", conn);
            var n = (long)(await cmd.ExecuteScalarAsync(cts.Token) ?? 0L);
            return new("postgres", "PostgreSQL", $"{n} 家供應商主檔", true, (int)sw.ElapsedMilliseconds, true);
        }
        catch (Exception ex)
        {
            return new("postgres", "PostgreSQL", Short(ex), false, (int)sw.ElapsedMilliseconds, true);
        }
    }

    private async Task<ServiceStatus> Http(string key, string name, string url, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient();
            http.Timeout = Probe;
            using var res = await http.GetAsync(url, ct);
            return new(key, name, res.IsSuccessStatusCode ? "正常" : $"HTTP {(int)res.StatusCode}",
                       res.IsSuccessStatusCode, (int)sw.ElapsedMilliseconds, true);
        }
        catch (Exception ex)
        {
            return new(key, name, Short(ex), false, (int)sw.ElapsedMilliseconds, true);
        }
    }

    /// <summary>
    /// Reachability without spending tokens: an unauthenticated request is
    /// enough, because a 401 still proves DNS, TLS and the route all work.
    /// </summary>
    private async Task<ServiceStatus> Llm(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var baseUri = new Uri(options.Primary.ApiUrl);
            var probe = new Uri(baseUri, "models");

            var http = httpFactory.CreateClient();
            http.Timeout = Probe;
            using var req = new HttpRequestMessage(HttpMethod.Get, probe);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Primary.ApiKey);

            using var res = await http.SendAsync(req, ct);
            var ok = res.IsSuccessStatusCode || res.StatusCode == System.Net.HttpStatusCode.Unauthorized;
            return new("llm", $"LLM · {options.Primary.Model}",
                       ok ? "可連線" : $"HTTP {(int)res.StatusCode}", ok, (int)sw.ElapsedMilliseconds, true);
        }
        catch (Exception ex)
        {
            return new("llm", $"LLM · {options.Primary.Model}", Short(ex), false, (int)sw.ElapsedMilliseconds, true);
        }
    }

    /// <summary>
    /// The SQL API only answers POST, so any reply at all proves the hop. It is
    /// not required for the default demo, which runs on mock master data.
    /// </summary>
    private async Task<ServiceStatus> Reachable(string key, string name, string url, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient();
            http.Timeout = Probe;
            using var res = await http.GetAsync(url, ct);
            return new(key, name, "可連線", true, (int)sw.ElapsedMilliseconds, false);
        }
        catch (Exception ex)
        {
            return new(key, name, Short(ex), false, (int)sw.ElapsedMilliseconds, false);
        }
    }

    private static string Short(Exception ex)
    {
        var msg = (ex.InnerException ?? ex).Message;
        return msg.Length <= 90 ? msg : msg[..90] + "…";
    }
}
