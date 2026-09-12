using System.Text.Json;
using System.Text.Json.Nodes;
using AgentApi.Models;
using AgentApi.Services.Llm;

namespace AgentApi.Services.Classification;

/// <summary>
/// Classifies one document and pulls out the fields mapping needs.
///
/// This lives in agent-api rather than behind an MCP tool so the JSON schema,
/// the validation, and the retry all stay under program control. The model
/// decides the category; code decides whether the answer is usable.
/// </summary>
public sealed class DocumentClassifier(
    LlmClient llm,
    AppOptions options,
    ILogger<DocumentClassifier> log)
{
    public static readonly string[] Categories =
    [
        "SupplierInvoice", "DebitNote", "QualityReport", "ShippingDocument",
        "EsgQuestionnaire", "Unknown",
    ];

    private const string SystemPrompt = """
        You classify procurement documents for a supply chain department.

        A document belongs to one of the business categories only if it is a
        complete structured record: it carries labelled fields, its own reference
        number, and the substance of the record filled in. Prose written to a
        person - a letter, an email, a notice, anything whose body is sentences
        rather than fields - is Unknown, even when it discusses invoices,
        deliveries, defects or inspections. Subject matter does not decide the
        category; structure and completeness do.

        Categories:
        - SupplierInvoice: an invoice from a supplier. Invoice number, line items, amounts.
        - DebitNote: a charge raised against a supplier, e.g. for late delivery or defects.
          Has a debit note number and an amount.
        - QualityReport: an inspection record. Has an inspection or lot reference and a
          result such as PASS or FAIL, laid out as fields.
        - ShippingDocument: a packing list, delivery note or bill of lading.
        - EsgQuestionnaire: a supplier sustainability survey return. Has a survey
          number and answered sections covering environment, social and governance
          topics. A return whose sections are largely unanswered, or that is still
          marked Draft, is not a completed record: classify it Unknown.
        - Unknown: free-form correspondence, incomplete returns, or anything that
          does not clearly fit above.

        Extraction rules:
        - Copy values exactly as printed. Never invent, reformat or re-punctuate an
          identifier, and never include surrounding quotes or commas in a value.
        - document_no is the record's own primary identifier: the invoice number, the
          debit note number, the inspection or lot reference, the shipment number,
          or the survey number.
        - part_no is the item or part number. If the document lists several line items,
          use the part number of the first line.
        - supplier_code is a vendor code. When the document names a vendor but gives no
          code, leave supplier_code null and put the name in supplier_name.
        - Use null for any field the document does not contain. Do not guess.
        - confidence is your certainty about the category only, from 0 to 1.
        """;

    private static JsonObject BuildSchema()
    {
        JsonObject Nullable(string type) => new()
        {
            ["type"] = new JsonArray { type, "null" },
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["category"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(Categories.Select(c => (JsonNode)c!).ToArray()),
                },
                ["confidence"] = new JsonObject { ["type"] = "number" },
                ["supplier_code"] = Nullable("string"),
                ["supplier_name"] = Nullable("string"),
                ["part_no"] = Nullable("string"),
                ["document_no"] = Nullable("string"),
            },
            ["required"] = new JsonArray
            {
                "category", "confidence", "supplier_code", "supplier_name", "part_no", "document_no",
            },
            ["additionalProperties"] = false,
        };
    }

    public async Task<ClassificationResult> ClassifyAsync(
        string filename, string content, CancellationToken ct = default)
    {
        var userPrompt = $"""
            File name: {filename}

            Document content:
            ---
            {content}
            ---

            Classify this document and extract the fields.
            """;

        var messages = new List<LlmMessage>
        {
            LlmMessage.System(SystemPrompt),
            LlmMessage.User(userPrompt),
        };

        // One retry: if the reply does not validate, hand the model its own
        // output plus the specific problem rather than repeating the prompt.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var result = await llm.CompleteAsync(new LlmRequest
            {
                Messages = messages,
                JsonSchema = BuildSchema(),
                JsonSchemaName = "document_classification",
                MaxTokens = options.LlmMaxTokensClassify,
                Temperature = 0.0,
            }, ct);

            if (TryParse(result.Content, out var parsed, out var problem))
            {
                log.LogInformation("Classified {File} as {Category} ({Confidence}) in {Ms}ms via {Provider}",
                    filename, parsed!.Category, parsed.Confidence, result.LatencyMs, result.Provider);

                return parsed with
                {
                    Filename = filename,
                    Provider = result.Provider,
                    Model = result.Model,
                    LatencyMs = result.LatencyMs,
                    Reasoning = result.Reasoning,
                };
            }

            log.LogWarning("Classification of {File} failed validation on attempt {Attempt}: {Problem}",
                filename, attempt, problem);

            if (attempt == 2)
            {
                // Never fail the pipeline on a bad reply. An Unknown at zero
                // confidence routes the document to manual review, which is the
                // correct outcome for something the model could not classify.
                return new ClassificationResult
                {
                    Filename = filename,
                    Category = "Unknown",
                    Confidence = 0,
                    Provider = result.Provider,
                    Model = result.Model,
                    LatencyMs = result.LatencyMs,
                    Error = problem,
                };
            }

            messages.Add(LlmMessage.Assistant(result.Content));
            messages.Add(LlmMessage.User(
                $"That reply was not valid: {problem}. Reply again with only the JSON object required by the schema."));
        }

        throw new InvalidOperationException("unreachable");
    }

    private static bool TryParse(string? content, out ClassificationResult? result, out string problem)
    {
        result = null;
        problem = "";

        if (string.IsNullOrWhiteSpace(content))
        {
            problem = "the reply was empty";
            return false;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(ExtractJson(content));
        }
        catch (JsonException ex)
        {
            problem = $"the reply was not valid JSON ({ex.Message})";
            return false;
        }

        if (node is not JsonObject obj)
        {
            problem = "the reply was not a JSON object";
            return false;
        }

        var category = obj["category"]?.GetValue<string>();
        if (category is null || !Categories.Contains(category))
        {
            problem = $"category must be one of {string.Join(", ", Categories)}";
            return false;
        }

        double confidence;
        try
        {
            confidence = obj["confidence"]?.GetValue<double>() ?? 0;
        }
        catch (Exception)
        {
            problem = "confidence must be a number";
            return false;
        }

        result = new ClassificationResult
        {
            Filename = "",
            Category = category,
            Confidence = Math.Clamp(confidence, 0, 1),
            SupplierCode = Str(obj, "supplier_code"),
            SupplierName = Str(obj, "supplier_name"),
            PartNo = Str(obj, "part_no"),
            DocumentNo = Str(obj, "document_no"),
        };
        return true;
    }

    private static readonly char[] StrayPunctuation = "\"',;. ".ToCharArray();

    private static string? Str(JsonObject obj, string key)
    {
        var v = obj[key];
        if (v is null) return null;
        var s = v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToString();
        if (string.IsNullOrWhiteSpace(s)) return null;

        // The model occasionally carries source punctuation into a copied
        // identifier, e.g. a trailing quote or comma. Trim it rather than store it.
        s = s.Trim().Trim(StrayPunctuation);
        return string.IsNullOrWhiteSpace(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    /// <summary>Strip a markdown fence if the model wrapped its JSON in one.</summary>
    private static string ExtractJson(string content)
    {
        var text = content.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline > 0) text = text[(firstNewline + 1)..];
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) text = text[..lastFence];
            text = text.Trim();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}
