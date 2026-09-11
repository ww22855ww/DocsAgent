namespace MockPortal.Models;

/// <summary>
/// The fixed demo document set.
///
/// The query deliberately ignores the date parameter: the demo must return the
/// same four files on any day it is run. Department and document type do filter,
/// so the portal still behaves like a real query screen.
/// </summary>
public sealed class MockDocumentStore
{
    private readonly string _dataDir;

    public MockDocumentStore(IWebHostEnvironment env)
        => _dataDir = Path.Combine(env.ContentRootPath, "MockData");

    private static readonly DocumentRecord[] All =
    [
        new("supplier_001.xlsx", "INV-001",      "SCM", "Invoice",       "Foxlink Precision", "2026-09-11", "5.2 KB"),
        new("quality_002.xlsx",  "IQC-99123",    "SCM", "QualityReport", "ACME",              "2026-09-11", "5.1 KB"),
        new("debit_003.csv",     "DN-2026-0093", "SCM", "DebitNote",     "Delta Components",  "2026-09-11", "102 B"),
        new("unknown_004.txt",   "-",            "SCM", "Other",         "ABC Corp.",         "2026-09-11", "315 B"),
    ];

    public IReadOnlyList<DocumentRecord> Query(string department, string documentType)
    {
        IEnumerable<DocumentRecord> q = All;

        if (!string.IsNullOrWhiteSpace(department) &&
            !department.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            q = q.Where(d => d.Department.Equals(department, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(documentType) &&
            !documentType.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            q = q.Where(d => d.DocumentType.Equals(documentType, StringComparison.OrdinalIgnoreCase));
        }

        return q.ToList();
    }

    /// <summary>
    /// Resolves a requested file name to a path inside MockData.
    /// Only names present in the fixed set are accepted, which also rules out
    /// path traversal.
    /// </summary>
    public string? ResolvePath(string? fileName)
    {
        var match = All.FirstOrDefault(d =>
            d.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;

        var full = Path.Combine(_dataDir, match.FileName);
        return File.Exists(full) ? full : null;
    }

    public static string ContentTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".csv"  => "text/csv",
        ".txt"  => "text/plain",
        _       => "application/octet-stream",
    };
}
