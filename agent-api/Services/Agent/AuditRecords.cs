using System.Text.Json.Nodes;

namespace AgentApi.Services.Agent;

/// <summary>One classification, captured for the task record.</summary>
public sealed record ClassificationRecord
{
    public required string Filename { get; init; }
    public required string Category { get; init; }
    public required double Confidence { get; init; }
    public string? SupplierCode { get; init; }
    public string? SupplierName { get; init; }
    public string? PartNo { get; init; }
    public string? DocumentNo { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public int LatencyMs { get; init; }
    public bool NeedsManualReview { get; init; }
    public string? ReviewReason { get; init; }
}

/// <summary>One supplier or part lookup, resolved or not.</summary>
public sealed record MappingRecord
{
    public string? Filename { get; init; }
    public required string Kind { get; init; }     // supplier | part
    public JsonNode? Query { get; init; }
    public required string Match { get; init; }    // unique | ambiguous | not_found
    public string? Source { get; init; }
    public JsonNode? Resolved { get; init; }
}
