namespace AgentApi.Models;

/// <summary>Runtime switches read from environment (docker compose env_file).</summary>
public sealed class AppOptions
{
    public string AgentMode { get; init; } = "scripted";   // scripted | llm
    public int MaxSteps { get; init; } = 30;
    public int ToolTimeoutSec { get; init; } = 60;
    public int TaskTimeoutMin { get; init; } = 10;

    public string LlmProvider { get; init; } = "ollama";    // ollama | openrouter
    public string LlmApiUrl { get; init; } = "";
    public string LlmApiKey { get; init; } = "";
    public string LlmModel { get; init; } = "";
    public int LlmTimeoutSec { get; init; } = 120;
    public int LlmMaxTokensTool { get; init; } = 1000;
    public int LlmMaxTokensClassify { get; init; } = 1500;

    public string McpUrl { get; init; } = "http://mcp-worker:8000/mcp";
    public string PostgresConnectionString { get; init; } = "";

    public static AppOptions FromConfiguration(IConfiguration c)
    {
        var provider = (c["LLM_PROVIDER"] ?? "ollama").Trim().ToLowerInvariant();
        var prefix = provider == "openrouter" ? "LLM_OPENROUTER" : "LLM_OLLAMA";

        return new AppOptions
        {
            AgentMode = (c["AGENT_MODE"] ?? "scripted").Trim().ToLowerInvariant(),
            MaxSteps = ParseInt(c["AGENT_MAX_STEPS"], 30),
            ToolTimeoutSec = ParseInt(c["AGENT_TOOL_TIMEOUT_SEC"], 60),
            TaskTimeoutMin = ParseInt(c["AGENT_TASK_TIMEOUT_MIN"], 10),

            LlmProvider = provider,
            LlmApiUrl = c[$"{prefix}_API_URL"] ?? "",
            LlmApiKey = c[$"{prefix}_API_KEY"] ?? "",
            LlmModel = c[$"{prefix}_MODEL"] ?? "",
            LlmTimeoutSec = ParseInt(c[$"{prefix}_TIMEOUT_SEC"], 120),
            LlmMaxTokensTool = ParseInt(c["LLM_MAX_TOKENS_TOOL"], 1000),
            LlmMaxTokensClassify = ParseInt(c["LLM_MAX_TOKENS_CLASSIFY"], 1500),

            McpUrl = c["MCP_URL"] ?? "http://mcp-worker:8000/mcp",
            PostgresConnectionString = c.GetConnectionString("Postgres") ?? "",
        };
    }

    private static int ParseInt(string? raw, int fallback)
        => int.TryParse(raw?.Trim(), out var v) ? v : fallback;
}
