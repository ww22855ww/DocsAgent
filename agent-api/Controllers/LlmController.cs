using System.Text.Json.Nodes;
using AgentApi.Models;
using AgentApi.Services.Classification;
using AgentApi.Services.Llm;
using Microsoft.AspNetCore.Mvc;

namespace AgentApi.Controllers;

/// <summary>
/// Phase 3 verification surface. These endpoints exercise the LLM integration
/// on its own, before the agent loop exists to exercise it for us.
/// </summary>
[ApiController]
[Route("api/llm")]
public sealed class LlmController(
    LlmClient llm,
    DocumentClassifier classifier,
    AppOptions options) : ControllerBase
{
    /// <summary>Which providers are configured and which one is active.</summary>
    [HttpGet("providers")]
    public IActionResult Providers() => Ok(new
    {
        primary = Describe(options.Primary),
        fallback = options.Fallback is null ? null : Describe(options.Fallback),
        maxTokens = new { tool = options.LlmMaxTokensTool, classify = options.LlmMaxTokensClassify },
    });

    /// <summary>Round-trip one prompt. Confirms auth, connectivity and latency.</summary>
    [HttpPost("ping")]
    public async Task<IActionResult> Ping([FromBody] PingRequest? body, CancellationToken ct)
    {
        var prompt = string.IsNullOrWhiteSpace(body?.Prompt)
            ? "Reply with the single word OK."
            : body!.Prompt!;

        try
        {
            var result = await llm.CompleteAsync(new LlmRequest
            {
                Messages = [LlmMessage.User(prompt)],
                MaxTokens = options.LlmMaxTokensTool,
            }, ct);

            return Ok(new
            {
                provider = result.Provider,
                model = result.Model,
                content = result.Content,
                finishReason = result.FinishReason,
                latencyMs = result.LatencyMs,
                usage = result.Usage,
                reasoningChars = result.Reasoning?.Length ?? 0,
            });
        }
        catch (LlmException ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>Classify a document body. The core Phase 3 acceptance check.</summary>
    [HttpPost("classify")]
    public async Task<IActionResult> Classify([FromBody] ClassifyRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Content))
            return BadRequest(new { error = "content is required" });

        try
        {
            var result = await classifier.ClassifyAsync(body.Filename ?? "document.txt", body.Content, ct);
            var (needsReview, reason) = result.NeedsManualReview();

            return Ok(new
            {
                result.Filename,
                result.Category,
                result.Confidence,
                result.SupplierCode,
                result.SupplierName,
                result.PartNo,
                result.DocumentNo,
                result.Provider,
                result.Model,
                result.LatencyMs,
                result.Error,
                manualReview = new { required = needsReview, reason },
            });
        }
        catch (LlmException ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Offer the model the real tool set and see which one it picks for a task.
    /// Verifies native tool calling works through the internal gateway.
    /// </summary>
    [HttpPost("tool-selection")]
    public async Task<IActionResult> ToolSelection([FromBody] PingRequest? body, CancellationToken ct)
    {
        var task = string.IsNullOrWhiteSpace(body?.Prompt) ? "處理今天 SCM 文件" : body!.Prompt!;
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        try
        {
            var result = await llm.CompleteAsync(new LlmRequest
            {
                Messages =
                [
                    LlmMessage.System(
                        $"You are a document processing agent. Today is {today}. " +
                        "Use the provided tools to accomplish the user's task. Call one tool at a time."),
                    LlmMessage.User(task),
                ],
                Tools = SampleTools(),
                MaxTokens = options.LlmMaxTokensTool,
            }, ct);

            return Ok(new
            {
                provider = result.Provider,
                model = result.Model,
                finishReason = result.FinishReason,
                latencyMs = result.LatencyMs,
                usage = result.Usage,
                toolCalls = result.ToolCalls.Select(tc => new
                {
                    tc.Id,
                    name = tc.Function.Name,
                    arguments = tc.ParseArguments(),
                }),
                content = result.Content,
            });
        }
        catch (LlmException ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// The real MCP tool names and shapes, hand-written here for Phase 3.
    /// Phase 4 replaces this with definitions discovered from the MCP worker.
    /// </summary>
    private static List<LlmToolDefinition> SampleTools() =>
    [
        new("download_documents",
            "Log into the document portal, search for documents and download them into staging.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["date"] = new JsonObject { ["type"] = "string", ["description"] = "Query date, YYYY-MM-DD" },
                    ["department"] = new JsonObject { ["type"] = "string", ["description"] = "Department code, e.g. SCM" },
                    ["document_type"] = new JsonObject { ["type"] = "string", ["description"] = "ALL, Invoice, QualityReport, DebitNote or Other" },
                },
                ["required"] = new JsonArray { "date", "department" },
            }),
        new("list_staging_files",
            "List the documents currently waiting in staging.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
        new("extract_documents",
            "Read staged documents and return their text content for classification.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["filenames"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Files to parse. Omit to parse everything in staging.",
                    },
                },
            }),
        new("search_supplier",
            "Look up a supplier by code and/or name. Use the name when the document has no code.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["supplier_code"] = new JsonObject { ["type"] = "string" },
                    ["supplier_name"] = new JsonObject { ["type"] = "string" },
                },
            }),
    ];

    private static object Describe(LlmProviderOptions p) => new
    {
        p.Name,
        p.Model,
        p.TimeoutSec,
        configured = p.IsConfigured,
        endpoint = p.ApiUrl,
    };

    public sealed record PingRequest(string? Prompt);
    public sealed record ClassifyRequest(string? Filename, string Content);
}
