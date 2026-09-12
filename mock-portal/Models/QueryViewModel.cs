namespace MockPortal.Models;

public sealed class QueryViewModel
{
    public string QueryDate { get; set; } = "";
    public string Department { get; set; } = "SCM";
    public string DocumentType { get; set; } = "ALL";

    public bool Searched { get; set; }
    public IReadOnlyList<DocumentRecord> Results { get; set; } = Array.Empty<DocumentRecord>();

    public static readonly string[] Departments = ["SCM", "QA", "FIN", "MFG"];
    public static readonly string[] DocumentTypes = ["ALL", "Invoice", "QualityReport", "DebitNote", "Other"];
}

public sealed class EsgQueryViewModel
{
    public string Year { get; set; } = "2026";
    public string Status { get; set; } = "ALL";

    public bool Searched { get; set; }
    public IReadOnlyList<EsgSurveyRecord> Results { get; set; } = Array.Empty<EsgSurveyRecord>();

    public static readonly string[] Years = ["2026", "2025", "2024"];
    public static readonly string[] Statuses = ["ALL", "Submitted", "Draft"];
}
