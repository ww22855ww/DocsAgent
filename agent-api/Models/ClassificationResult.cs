namespace AgentApi.Services.Classification;

/// <summary>What the classifier produced for one document.</summary>
public sealed record ClassificationResult
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

    /// <summary>The model's chain of thought. Shown in the trace, never used as the answer.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Set when the reply could not be validated and the result was forced to Unknown.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Whether a human has to look at this.
    ///
    /// Rules first, confidence last. A self-reported confidence from an
    /// uncalibrated model is weak evidence, so a missing category or a missing
    /// identifier decides the outcome before the number does. See roadmap.md
    /// section 10.
    /// </summary>
    public (bool Required, string? Reason) NeedsManualReview(double threshold = 0.80)
    {
        if (Error is not null)
            return (true, $"classification failed: {Error}");

        if (Category == "Unknown")
            return (true, "category is Unknown");

        if (SupplierCode is null && SupplierName is null)
            return (true, "no supplier code or supplier name could be extracted");

        // Every business category is a record with its own reference number. A
        // classification into one of them without that number means the model
        // labelled something that is not really that document, so the label is
        // not trustworthy regardless of the confidence it reported.
        if (DocumentNo is null)
            return (true, $"classified as {Category} but no document number was found");

        if (Confidence < threshold)
            return (true, $"confidence {Confidence:0.00} is below the {threshold:0.00} threshold");

        return (false, null);
    }
}
