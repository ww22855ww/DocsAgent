using System.Text.Json;
using System.Text.Json.Nodes;
using AgentApi.Models;
using AgentApi.Services.Classification;
using AgentApi.Services.Llm;
using AgentApi.Services.Mcp;

namespace AgentApi.Services.Agent;

/// <summary>
/// Everything the agent may call: the tools mcp-worker exposes, plus
/// classify_document, which runs here in agent-api.
///
/// Classification stays local so the JSON schema, validation and retry remain
/// under program control (roadmap.md section 4.3). It takes only a filename:
/// the extracted text is held in the task context, so document bodies never
/// make a round trip through the model just to come back as an argument.
/// </summary>
public sealed class ToolRegistry(
    McpToolClient mcp,
    DocumentClassifier classifier,
    ILogger<ToolRegistry> log)
{
    public const string ClassifyTool = "classify_document";

    /// <summary>
    /// Tools the model is never offered, even though the worker exposes them.
    /// write_excel is reporting, not a decision: it runs once after the task
    /// finishes, so the agent cannot forget it, call it early, or call it twice.
    /// </summary>
    private static readonly HashSet<string> Hidden = ["ping", "write_excel"];

    public async Task<IReadOnlyList<LlmToolDefinition>> GetDefinitionsAsync(CancellationToken ct)
    {
        var tools = await mcp.ListToolsAsync(ct);

        var defs = tools
            .Where(t => !Hidden.Contains(t.Name))
            .Select(t => new LlmToolDefinition(
                t.Name,
                FirstParagraph(t.Description),
                JsonNode.Parse(t.JsonSchema.GetRawText()) as JsonObject ?? EmptySchema()))
            .ToList();

        defs.Add(new LlmToolDefinition(
            ClassifyTool,
            "Classify one already-extracted document and pull out its supplier, part and document number.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["filename"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "A file that extract_documents has already parsed.",
                    },
                },
                ["required"] = new JsonArray { "filename" },
            }));

        return defs;
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken ct)
    {
        if (name == ClassifyTool) return true;
        var tools = await mcp.ListToolsAsync(ct);
        return tools.Any(t => t.Name == name && !Hidden.Contains(t.Name));
    }

    /// <summary>Execute a tool. Local tools are handled here; everything else goes to MCP.</summary>
    public async Task<JsonNode> ExecuteAsync(
        AgentContext context, string name, JsonObject args, CancellationToken ct)
    {
        if (name == ClassifyTool)
            return await ClassifyAsync(context, args, ct);

        args = FillFromContext(context, name, args);

        var result = await mcp.CallAsync(name, args, ct);

        // Watch the traffic for what the task needs to remember: which documents
        // exist, what their text is, and which lookups resolved.
        switch (name)
        {
            case "download_documents": context.RememberDownloads(result); break;
            case "extract_documents": context.RememberExtractions(result); break;
            case "search_supplier": context.RememberSupplierLookup(result); RecordMapping(context, "supplier", result); break;
            case "search_part": context.RememberPartLookup(result); RecordMapping(context, "part", result); break;
        }

        return result;
    }

    /// <summary>
    /// Keep every lookup, resolved or not. The misses matter: they are the
    /// evidence for why a document went to manual review.
    /// </summary>
    private static void RecordMapping(AgentContext context, string kind, JsonNode? result)
    {
        if (result is null) return;

        var first = (result["results"] as JsonArray)?.FirstOrDefault();
        context.Mappings.Add(new MappingRecord
        {
            Kind = kind,
            Query = result["query"]?.DeepClone(),
            Match = result["match"]?.GetValue<string>() ?? "unknown",
            Source = first?["source"]?.GetValue<string>() ?? result["mode"]?.GetValue<string>(),
            Resolved = result["match"]?.GetValue<string>() == "unique" ? first?.DeepClone() : null,
        });
    }

    /// <summary>
    /// Supply the record-keeping arguments from task state instead of trusting
    /// what the caller passed.
    ///
    /// The agent is good at deciding that a document is ready to archive and bad
    /// at restating its classification: asked to hand back a classification
    /// object, gemma4 supplied an empty one and every archive column came out
    /// null. The identifiers are already known here, so they are written from
    /// here. Both runners go through this path, so both store the same shape.
    /// </summary>
    private static JsonObject FillFromContext(AgentContext context, string name, JsonObject args)
    {
        var filename = args["filename"]?.GetValue<string>();

        switch (name)
        {
            case "archive_record" when !string.IsNullOrWhiteSpace(filename):
                return new JsonObject
                {
                    ["filename"] = filename,
                    ["classification"] = context.BuildClassification(filename!),
                    ["mapping"] = context.BuildMapping(filename!),
                    ["task_id"] = context.Task.Id,
                };

            case "notify_manual_review" when !string.IsNullOrWhiteSpace(filename):
                var copy = args.DeepClone().AsObject();
                copy["task_id"] = context.Task.Id;
                return copy;

            default:
                return args;
        }
    }

    private async Task<JsonNode> ClassifyAsync(AgentContext context, JsonObject args, CancellationToken ct)
    {
        var filename = args["filename"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(filename))
            return Error("filename is required.");

        if (!context.TryGetExtractedText(filename, out var content))
        {
            return Error(
                $"{filename} has not been extracted yet. Call extract_documents first, " +
                "then classify. Available: " +
                (context.ExtractedFilenames.Count == 0
                    ? "(nothing extracted yet)"
                    : string.Join(", ", context.ExtractedFilenames)));
        }

        var result = await classifier.ClassifyAsync(filename, content!, ct);
        var (needsReview, reason) = result.NeedsManualReview();

        var outcome = context.Outcome(filename);
        outcome.Category = result.Category;
        outcome.Confidence = result.Confidence;
        outcome.SupplierCode = result.SupplierCode;
        outcome.SupplierName = result.SupplierName;
        outcome.PartNo = result.PartNo;
        outcome.DocumentNo = result.DocumentNo;
        outcome.ReviewReason = reason;

        context.Classifications.Add(new ClassificationRecord
        {
            Filename = result.Filename,
            Category = result.Category,
            Confidence = result.Confidence,
            SupplierCode = result.SupplierCode,
            SupplierName = result.SupplierName,
            PartNo = result.PartNo,
            DocumentNo = result.DocumentNo,
            Provider = result.Provider,
            Model = result.Model,
            LatencyMs = result.LatencyMs,
            NeedsManualReview = needsReview,
            ReviewReason = reason,
        });

        log.LogInformation("classify_document {File} -> {Category} review={Review}",
            filename, result.Category, needsReview);

        return new JsonObject
        {
            ["filename"] = result.Filename,
            ["category"] = result.Category,
            ["confidence"] = result.Confidence,
            ["supplier_code"] = result.SupplierCode,
            ["supplier_name"] = result.SupplierName,
            ["part_no"] = result.PartNo,
            ["document_no"] = result.DocumentNo,
            ["needs_manual_review"] = needsReview,
            ["manual_review_reason"] = reason,
        };
    }

    private static JsonObject Error(string message)
        => new() { ["status"] = "error", ["error"] = message };

    private static JsonObject EmptySchema()
        => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    /// <summary>Tool descriptions carry a full docstring; the model only needs the first paragraph.</summary>
    private static string FirstParagraph(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        var text = description.Trim();
        var breakAt = text.IndexOf("\n\n", StringComparison.Ordinal);
        return (breakAt > 0 ? text[..breakAt] : text).Replace("\n", " ").Trim();
    }
}
