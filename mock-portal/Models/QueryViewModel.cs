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
