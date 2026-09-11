using System.Text.Json.Nodes;
using AgentApi.Models;

namespace AgentApi.Services.Agent;

/// <summary>
/// Per-task working state: the trace, what each document became, and the text
/// pulled out of each file.
///
/// Holding extracted text here is what keeps document bodies out of the model's
/// context. extract_documents returns the text once, the agent sees only a
/// summary of it, and classify_document reads it back from this cache.
/// </summary>
public sealed class AgentContext(AgentTask task, Action<TaskStep>? onStep = null)
{
    private readonly Dictionary<string, string> _extracted = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<JsonObject> _supplierHits = [];
    private readonly List<JsonObject> _partHits = [];

    public AgentTask Task => task;
    public IReadOnlyCollection<string> ExtractedFilenames => _extracted.Keys;

    public TaskStep AddStep(
        string kind,
        string title,
        string? detail = null,
        string? thought = null,
        string? toolName = null,
        JsonNode? arguments = null,
        JsonNode? result = null,
        bool success = true,
        int durationMs = 0)
    {
        var step = new TaskStep
        {
            Index = task.Steps.Count + 1,
            Kind = kind,
            Title = title,
            Detail = detail,
            Thought = thought,
            ToolName = toolName,
            Arguments = arguments,
            Result = result,
            Success = success,
            DurationMs = durationMs,
        };
        task.Steps.Add(step);
        onStep?.Invoke(step);
        return step;
    }

    public DocumentOutcome Outcome(string filename)
    {
        if (!task.Documents.TryGetValue(filename, out var outcome))
        {
            outcome = new DocumentOutcome { Filename = filename };
            task.Documents[filename] = outcome;
        }
        return outcome;
    }

    public bool TryGetExtractedText(string filename, out string? content)
        => _extracted.TryGetValue(filename, out content);

    /// <summary>Record the file list returned by download_documents.</summary>
    public void RememberDownloads(JsonNode? result)
    {
        if (result?["files"] is not JsonArray files) return;
        foreach (var f in files)
        {
            var name = f?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name)) Outcome(name!);
        }
    }

    /// <summary>Cache the text returned by extract_documents.</summary>
    public void RememberExtractions(JsonNode? result)
    {
        if (result?["documents"] is not JsonArray docs) return;
        foreach (var d in docs)
        {
            var name = d?["filename"]?.GetValue<string>();
            var content = d?["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || content is null) continue;
            _extracted[name!] = content;
            Outcome(name!);
        }
    }

    /// <summary>Keep a unique supplier lookup so it can be attached at archive time.</summary>
    public void RememberSupplierLookup(JsonNode? result)
    {
        if (result?["match"]?.GetValue<string>() != "unique") return;
        if ((result["results"] as JsonArray)?.FirstOrDefault()?.DeepClone() is JsonObject hit)
            _supplierHits.Add(hit);
    }

    /// <summary>Keep a unique part lookup so it can be attached at archive time.</summary>
    public void RememberPartLookup(JsonNode? result)
    {
        if (result?["match"]?.GetValue<string>() != "unique") return;
        if ((result["results"] as JsonArray)?.FirstOrDefault()?.DeepClone() is JsonObject hit)
            _partHits.Add(hit);
    }

    /// <summary>
    /// The classification this task produced for a document.
    ///
    /// Built here rather than taken from the agent's tool arguments. The agent
    /// decides when to archive; what gets written is not its to restate, and
    /// asking it to restate the data only invites an empty or invented object.
    /// </summary>
    public JsonObject BuildClassification(string filename)
    {
        var o = Outcome(filename);
        return new JsonObject
        {
            ["category"] = o.Category,
            ["confidence"] = o.Confidence,
            ["supplier_code"] = o.SupplierCode,
            ["supplier_name"] = o.SupplierName,
            ["part_no"] = o.PartNo,
            ["document_no"] = o.DocumentNo,
        };
    }

    /// <summary>
    /// The mapping that applies to a document, matched against its own extracted
    /// identifiers rather than simply taken from the most recent lookup.
    /// </summary>
    public JsonObject BuildMapping(string filename)
    {
        var o = Outcome(filename);

        var supplier = _supplierHits.LastOrDefault(h =>
            Eq(h["supplier_code"], o.SupplierCode) ||
            Contains(h["supplier_name"], o.SupplierName));

        var part = _partHits.LastOrDefault(h => Eq(h["part_no"], o.PartNo));

        // Adopt the authoritative names from the mapping, so the archive row
        // carries the master-data spelling rather than what the document said.
        if (supplier is not null)
        {
            o.SupplierCode = supplier["supplier_code"]?.GetValue<string>() ?? o.SupplierCode;
            o.SupplierName = supplier["supplier_name"]?.GetValue<string>() ?? o.SupplierName;
        }

        return new JsonObject
        {
            ["supplier"] = supplier?.DeepClone(),
            ["part"] = part?.DeepClone(),
        };
    }

    private static bool Eq(JsonNode? node, string? value)
        => value is not null && node?.GetValue<string>() is { } s &&
           s.Equals(value, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(JsonNode? node, string? value)
        => value is not null && node?.GetValue<string>() is { } s &&
           s.Contains(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shrink a tool result before it goes back to the model.
    ///
    /// extract_documents returns every document body. Feeding that back would
    /// spend thousands of tokens on text the model does not need to see, since
    /// classify_document reads it from the cache instead.
    /// </summary>
    public JsonNode Summarise(string toolName, JsonNode result)
    {
        if (toolName != "extract_documents" || result["documents"] is not JsonArray docs)
            return result;

        var slim = new JsonArray();
        foreach (var d in docs)
        {
            var name = d?["filename"]?.GetValue<string>() ?? "";
            var content = d?["content"]?.GetValue<string>() ?? "";
            slim.Add(new JsonObject
            {
                ["filename"] = name,
                ["chars"] = content.Length,
                ["error"] = d?["error"]?.DeepClone(),
            });
        }

        return new JsonObject
        {
            ["count"] = docs.Count,
            ["documents"] = slim,
            ["note"] = "Text is cached server-side. Call classify_document with a filename to classify it.",
        };
    }
}
