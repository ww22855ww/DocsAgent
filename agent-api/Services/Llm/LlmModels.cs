using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentApi.Services.Llm;

// ---------------------------------------------------------------------------
// Request
// ---------------------------------------------------------------------------

public sealed record LlmMessage(string Role, string? Content)
{
    /// <summary>Set on assistant messages that requested tools, so the history replays correctly.</summary>
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }

    /// <summary>Set on tool-result messages; must match the id of the call being answered.</summary>
    public string? ToolCallId { get; init; }

    public static LlmMessage System(string content) => new("system", content);
    public static LlmMessage User(string content) => new("user", content);
    public static LlmMessage Assistant(string? content) => new("assistant", content);

    public static LlmMessage ToolResult(string toolCallId, string content)
        => new("tool", content) { ToolCallId = toolCallId };
}

/// <summary>One tool offered to the model, in OpenAI function-calling shape.</summary>
public sealed record LlmToolDefinition(string Name, string Description, JsonObject ParametersSchema);

public sealed class LlmRequest
{
    public required IReadOnlyList<LlmMessage> Messages { get; init; }
    public IReadOnlyList<LlmToolDefinition>? Tools { get; init; }

    /// <summary>JSON Schema the reply must satisfy. Mutually exclusive with Tools in practice.</summary>
    public JsonObject? JsonSchema { get; init; }
    public string JsonSchemaName { get; init; } = "result";

    public int MaxTokens { get; init; } = 1000;
    public double Temperature { get; init; } = 0.1;
}

// ---------------------------------------------------------------------------
// Response
// ---------------------------------------------------------------------------

public sealed class LlmFunctionCall
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>A JSON document encoded as a string, per the OpenAI wire format.</summary>
    [JsonPropertyName("arguments")] public string Arguments { get; set; } = "";
}

public sealed class LlmToolCall
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public LlmFunctionCall Function { get; set; } = new();

    /// <summary>Parse Arguments, returning an empty object when the model emits something unusable.</summary>
    public JsonObject ParseArguments()
    {
        if (string.IsNullOrWhiteSpace(Function.Arguments)) return new JsonObject();
        try
        {
            return JsonNode.Parse(Function.Arguments) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}

public sealed class LlmResponseMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "assistant";
    [JsonPropertyName("content")] public string? Content { get; set; }

    /// <summary>gemma4 emits its chain of thought here. Useful for the trace, never parsed as an answer.</summary>
    [JsonPropertyName("reasoning")] public string? Reasoning { get; set; }

    [JsonPropertyName("tool_calls")] public List<LlmToolCall>? ToolCalls { get; set; }
}

public sealed class LlmChoice
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("message")] public LlmResponseMessage Message { get; set; } = new();
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

public sealed class LlmUsage
{
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
}

public sealed class LlmRawResponse
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("choices")] public List<LlmChoice> Choices { get; set; } = [];
    [JsonPropertyName("usage")] public LlmUsage? Usage { get; set; }
}

/// <summary>What the rest of the app consumes: one choice, plus which provider answered.</summary>
public sealed record LlmResult
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string? Content { get; init; }
    public string? Reasoning { get; init; }
    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = [];
    public string? FinishReason { get; init; }
    public LlmUsage? Usage { get; init; }
    public int LatencyMs { get; init; }

    /// <summary>True when the reply was cut off mid-generation, which usually means the token budget was too low.</summary>
    public bool TruncatedByLength => FinishReason == "length";

    public bool HasToolCalls => ToolCalls.Count > 0;
}

/// <summary>Raised when every configured provider failed.</summary>
public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);
