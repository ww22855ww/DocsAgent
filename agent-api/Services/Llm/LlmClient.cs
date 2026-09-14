using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentApi.Models;

namespace AgentApi.Services.Llm;

/// <summary>
/// Calls an OpenAI-compatible /chat/completions endpoint, and falls back to the
/// other configured provider once if the primary fails.
///
/// Both the internal Ollama gateway and OpenRouter speak this format, so the
/// only difference between them is configuration. That is what makes the
/// fallback a one-line switch rather than a second integration.
/// </summary>
public sealed class LlmClient(
    AppOptions options,
    IHttpClientFactory httpFactory,
    ILogger<LlmClient> log)
{
    private static readonly JsonSerializerOptions ReadJson = new(JsonSerializerDefaults.Web);

    public LlmProviderOptions Primary => options.Primary;
    public LlmProviderOptions? Fallback => options.Fallback;

    public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (!options.Primary.IsConfigured)
            throw new LlmException($"LLM provider '{options.Primary.Name}' is not configured.");

        try
        {
            return await CallAsync(options.Primary, request, ct);
        }
        catch (Exception primaryEx) when (primaryEx is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (options.Fallback is null)
                throw new LlmException($"LLM provider '{options.Primary.Name}' failed and no fallback is configured.", primaryEx);

            log.LogWarning(primaryEx, "LLM provider {Primary} failed, falling back to {Fallback}",
                options.Primary.Name, options.Fallback.Name);

            try
            {
                return await CallAsync(options.Fallback, request, ct);
            }
            catch (Exception fallbackEx)
            {
                throw new LlmException(
                    $"Both LLM providers failed. {options.Primary.Name}: {primaryEx.Message}; " +
                    $"{options.Fallback.Name}: {fallbackEx.Message}", fallbackEx);
            }
        }
    }

    private async Task<LlmResult> CallAsync(LlmProviderOptions provider, LlmRequest request, CancellationToken ct)
    {
        var payload = BuildPayload(provider, request);

        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(provider.TimeoutSec);

        using var message = new HttpRequestMessage(HttpMethod.Post, provider.ApiUrl)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        var sw = Stopwatch.StartNew();
        using var response = await http.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{provider.Name} returned HTTP {(int)response.StatusCode}: {Trim(body, 300)}");
        }

        var raw = JsonSerializer.Deserialize<LlmRawResponse>(body, ReadJson)
                  ?? throw new LlmException($"{provider.Name} returned an unreadable response.");

        var choice = raw.Choices.FirstOrDefault()
                     ?? throw new LlmException($"{provider.Name} returned no choices.");

        var result = new LlmResult
        {
            Provider = provider.Name,
            Model = raw.Model ?? provider.Model,
            Content = choice.Message.Content,
            Reasoning = choice.Message.Reasoning,
            ToolCalls = choice.Message.ToolCalls ?? [],
            FinishReason = choice.FinishReason,
            Usage = raw.Usage,
            LatencyMs = (int)sw.ElapsedMilliseconds,
        };

        log.LogInformation(
            "LLM {Provider}/{Model} finish={Finish} tools={Tools} tokens={Tokens} in {Ms}ms",
            result.Provider, result.Model, result.FinishReason, result.ToolCalls.Count,
            result.Usage?.TotalTokens, result.LatencyMs);

        // The model spends tokens on a reasoning field before emitting content.
        // Running out mid-reasoning yields an empty content string and no error,
        // which is very hard to diagnose downstream. Surface it here instead.
        if (result.TruncatedByLength && string.IsNullOrWhiteSpace(result.Content) && !result.HasToolCalls)
        {
            throw new LlmException(
                $"{provider.Name} hit the {request.MaxTokens}-token limit while still reasoning and " +
                "produced no answer. Raise the token budget for this call.");
        }

        return result;
    }

    private static JsonObject BuildPayload(LlmProviderOptions provider, LlmRequest request)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            var node = new JsonObject { ["role"] = m.Role };

            // Always send content, even empty. The OpenAI spec lets an assistant
            // message omit it when tool_calls is present, and the internal
            // gateway usually accepts that, but one of the instances behind it
            // validates strictly and rejects the whole history with
            // "messages.2.content Field required". Sending "" satisfies both,
            // and a failure here is expensive: the request 400s, the fallback
            // provider is tried, and a demo stalls for minutes.
            node["content"] = m.Content ?? "";

            if (m.ToolCallId is not null) node["tool_call_id"] = m.ToolCallId;

            if (m.ToolCalls is { Count: > 0 })
            {
                var calls = new JsonArray();
                foreach (var tc in m.ToolCalls)
                {
                    calls.Add(new JsonObject
                    {
                        ["id"] = tc.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tc.Function.Name,
                            ["arguments"] = tc.Function.Arguments,
                        },
                    });
                }
                node["tool_calls"] = calls;
            }

            messages.Add(node);
        }

        var payload = new JsonObject
        {
            ["model"] = provider.Model,
            ["messages"] = messages,
            ["stream"] = false,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
        };

        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = t.ParametersSchema.DeepClone(),
                    },
                });
            }
            payload["tools"] = tools;
            payload["tool_choice"] = "auto";
        }

        if (request.JsonSchema is not null)
        {
            payload["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.JsonSchemaName,
                    ["strict"] = true,
                    ["schema"] = request.JsonSchema.DeepClone(),
                },
            };
        }

        return payload;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
