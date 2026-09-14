using System.Text.Json.Nodes;

namespace AgentApi.Models;

public enum TaskState
{
    Created,
    Running,
    WaitingLlm,
    WaitingTool,
    ManualReview,
    Completed,
    Failed,
}

/// <summary>One entry in the execution trace. The UI renders these in order.</summary>
public sealed class TaskStep
{
    public required int Index { get; init; }
    public required string Kind { get; init; }        // agent | tool | classify | summary | error
    public required string Title { get; init; }
    public string? Detail { get; init; }

    /// <summary>The model's stated intent before a tool call, when it gave one.</summary>
    public string? Thought { get; init; }

    public string? ToolName { get; init; }
    public JsonNode? Arguments { get; init; }
    public JsonNode? Result { get; init; }

    public bool Success { get; init; } = true;
    public int DurationMs { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Who chose to take this step: "model" when the agent picked the tool,
    /// "script" when the fixed sequence did. The UI badges this, which is how
    /// the audience sees the two modes differ without being told.
    /// </summary>
    public string? DecidedBy { get; init; }

    /// <summary>
    /// A short label for a step that carries the argument, e.g. the moment the
    /// agent recovers from a missing code or the moment the fixed flow runs out
    /// of options. Rendered as a callout, so use it sparingly.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>Whether Note marks a good outcome or a limitation.</summary>
    public string? NoteTone { get; init; }

    /// <summary>
    /// Which layer carried the step out: "mcp" for a tool on the MCP worker,
    /// "model" for classification, which runs in agent-api against the LLM.
    ///
    /// Separate from DecidedBy on purpose. Every tool step in agent mode was
    /// chosen by the model, but only classification is performed by it, and the
    /// audience cannot tell those apart unless the trace says so.
    /// </summary>
    public string? ExecutedBy { get; init; }
}

/// <summary>What happened to one document as it moved through the pipeline.</summary>
public sealed class DocumentOutcome
{
    public required string Filename { get; init; }
    public string? Category { get; set; }
    public double? Confidence { get; set; }
    public string? SupplierCode { get; set; }
    public string? SupplierName { get; set; }
    public string? PartNo { get; set; }
    public string? DocumentNo { get; set; }

    /// <summary>archived | manual_review | pending</summary>
    public string Status { get; set; } = "pending";
    public string? ReviewReason { get; set; }
    public bool Notified { get; set; }
}

public sealed class AgentTask
{
    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public required string Mode { get; init; }        // scripted | llm

    public TaskState State { get; set; } = TaskState.Created;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    public List<TaskStep> Steps { get; } = [];
    public Dictionary<string, DocumentOutcome> Documents { get; } = [];

    public string? Summary { get; set; }
    public string? Error { get; set; }

    public int DurationMs => (int)((FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt).TotalMilliseconds;

    public object BuildSummaryCounts()
    {
        var docs = Documents.Values.ToList();
        return new
        {
            downloaded = docs.Count,
            classified = docs.Count(d => d.Category is not null),
            archived = docs.Count(d => d.Status == "archived"),
            manualReview = docs.Count(d => d.Status == "manual_review"),
            notificationsSent = docs.Count(d => d.Notified),
            byCategory = docs.Where(d => d.Category is not null)
                             .GroupBy(d => d.Category!)
                             .ToDictionary(g => g.Key, g => g.Count()),
        };
    }
}
