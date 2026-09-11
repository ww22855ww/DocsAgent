namespace AgentApi.Models;

/// <summary>One OpenAI-compatible chat completions endpoint.</summary>
public sealed class LlmProviderOptions
{
    public required string Name { get; init; }
    public required string ApiUrl { get; init; }
    public required string ApiKey { get; init; }
    public required string Model { get; init; }
    public int TimeoutSec { get; init; } = 120;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiUrl)
                             && !string.IsNullOrWhiteSpace(ApiKey)
                             && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>Runtime switches read from environment (docker compose env_file).</summary>
public sealed class AppOptions
{
    public string AgentMode { get; init; } = "scripted";   // scripted | llm
    public int MaxSteps { get; init; } = 30;
    public int ToolTimeoutSec { get; init; } = 60;
    public int TaskTimeoutMin { get; init; } = 10;

    /// <summary>The provider LLM_PROVIDER selects.</summary>
    public required LlmProviderOptions Primary { get; init; }

    /// <summary>The other configured provider, tried once if the primary fails.</summary>
    public LlmProviderOptions? Fallback { get; init; }

    // gemma4 always emits reasoning tokens before content, so these budgets
    // must stay generous. Too low returns finish_reason=length with empty
    // content and no error. See CLAUDE.md.
    public int LlmMaxTokensTool { get; init; } = 1000;
    public int LlmMaxTokensClassify { get; init; } = 1500;

    public string McpUrl { get; init; } = "http://mcp-worker:8000/mcp";
    public string PostgresConnectionString { get; init; } = "";

    public static AppOptions FromConfiguration(IConfiguration c)
    {
        var ollama = ReadProvider(c, "ollama", "LLM_OLLAMA");
        var openrouter = ReadProvider(c, "openrouter", "LLM_OPENROUTER");

        var selected = (c["LLM_PROVIDER"] ?? "ollama").Trim().ToLowerInvariant();
        var (primary, other) = selected == "openrouter"
            ? (openrouter, ollama)
            : (ollama, openrouter);

        return new AppOptions
        {
            AgentMode = (c["AGENT_MODE"] ?? "scripted").Trim().ToLowerInvariant(),
            MaxSteps = ParseInt(c["AGENT_MAX_STEPS"], 30),
            ToolTimeoutSec = ParseInt(c["AGENT_TOOL_TIMEOUT_SEC"], 60),
            TaskTimeoutMin = ParseInt(c["AGENT_TASK_TIMEOUT_MIN"], 10),

            Primary = primary,
            Fallback = other.IsConfigured ? other : null,

            LlmMaxTokensTool = ParseInt(c["LLM_MAX_TOKENS_TOOL"], 1000),
            LlmMaxTokensClassify = ParseInt(c["LLM_MAX_TOKENS_CLASSIFY"], 1500),

            McpUrl = c["MCP_URL"] ?? "http://mcp-worker:8000/mcp",
            PostgresConnectionString = c.GetConnectionString("Postgres") ?? "",
        };
    }

    private static LlmProviderOptions ReadProvider(IConfiguration c, string name, string prefix) => new()
    {
        Name = name,
        ApiUrl = c[$"{prefix}_API_URL"] ?? "",
        ApiKey = c[$"{prefix}_API_KEY"] ?? "",
        Model = c[$"{prefix}_MODEL"] ?? "",
        TimeoutSec = ParseInt(c[$"{prefix}_TIMEOUT_SEC"], 120),
    };

    private static int ParseInt(string? raw, int fallback)
        => int.TryParse(raw?.Trim(), out var v) ? v : fallback;
}
