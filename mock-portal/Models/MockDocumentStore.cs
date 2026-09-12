namespace MockPortal.Models;

/// <summary>
/// The fixed demo document set, in two sections.
///
/// SCM documents and ESG questionnaires are separate screens with separate
/// query forms and separate download routes, so fetching one is a different
/// path through the site than fetching the other. That is what gives the agent
/// a routing decision to make rather than a single entry point.
///
/// Queries deliberately ignore the date and year: the demo must return the same
/// files on any day it is run. The other filters do apply, so each screen still
/// behaves like a real query form.
/// </summary>
public sealed class MockDocumentStore
{
    private readonly string _dataDir;

    public MockDocumentStore(IWebHostEnvironment env)
        => _dataDir = Path.Combine(env.ContentRootPath, "MockData");

    private static readonly DocumentRecord[] Documents =
    [
        new("supplier_001.xlsx", "INV-2026-0912", "SCM", "Invoice",       "華碩電腦股份有限公司", "2026-09-12", "5.3 KB"),
        new("quality_002.xlsx",  "LOT-26-99123",  "SCM", "QualityReport", "勝宏科技",            "2026-09-12", "5.1 KB"),
        new("debit_003.csv",     "DN-2026-0093",  "SCM", "DebitNote",     "聯強國際股份有限公司", "2026-09-12", "148 B"),
        new("unknown_004.txt",   "-",             "SCM", "Other",         "至上電子股份有限公司", "2026-09-12", "382 B"),
    ];

    private static readonly EsgSurveyRecord[] Surveys =
    [
        new("esg_3707.xlsx",      "ESG-2026-3707", "3707", "華碩電腦股份有限公司",       "Submitted", "2026-09-12", "5.4 KB"),
        new("esg_9414.xlsx",      "ESG-2026-9414", "9414", "瀚宇博德科技(江陰)有限公司", "Submitted", "2026-09-12", "5.4 KB"),
        new("esg_draft_005.xlsx", "ESG-2026-",     "",     "麗臺科技",                   "Draft",     "-",          "5.4 KB"),
    ];

    public IReadOnlyList<DocumentRecord> QueryDocuments(string department, string documentType)
    {
        IEnumerable<DocumentRecord> q = Documents;

        if (!IsAll(department))
            q = q.Where(d => d.Department.Equals(department, StringComparison.OrdinalIgnoreCase));

        if (!IsAll(documentType))
            q = q.Where(d => d.DocumentType.Equals(documentType, StringComparison.OrdinalIgnoreCase));

        return q.ToList();
    }

    public IReadOnlyList<EsgSurveyRecord> QuerySurveys(string status)
    {
        IEnumerable<EsgSurveyRecord> q = Surveys;

        if (!IsAll(status))
            q = q.Where(s => s.Status.Equals(status, StringComparison.OrdinalIgnoreCase));

        return q.ToList();
    }

    /// <summary>
    /// Resolves a requested file name to a path inside MockData, scoped to one
    /// section. Only names in that section's fixed list are accepted, which also
    /// rules out path traversal and stops one section serving the other's files.
    /// </summary>
    public string? ResolvePath(PortalSection section, string? fileName)
    {
        var known = section == PortalSection.EsgSurveys
            ? Surveys.Select(s => s.FileName)
            : Documents.Select(d => d.FileName);

        var match = known.FirstOrDefault(n => n.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;

        var full = Path.Combine(_dataDir, match);
        return File.Exists(full) ? full : null;
    }

    private static bool IsAll(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Equals("ALL", StringComparison.OrdinalIgnoreCase);

    public static string ContentTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".csv"  => "text/csv",
        ".txt"  => "text/plain",
        _       => "application/octet-stream",
    };
}
