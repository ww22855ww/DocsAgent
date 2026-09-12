namespace MockPortal.Models;

/// <summary>Which portal section a file is served from. Each has its own screen.</summary>
public enum PortalSection
{
    ScmDocuments,
    EsgSurveys,
}

/// <summary>One row in the SCM document query result list.</summary>
public sealed record DocumentRecord(
    string FileName,
    string DocumentNo,
    string Department,
    string DocumentType,
    string Vendor,
    string IssuedOn,
    string SizeLabel);

/// <summary>One row in the ESG questionnaire result list.</summary>
public sealed record EsgSurveyRecord(
    string FileName,
    string SurveyNo,
    string SupplierCode,
    string SupplierName,
    string Status,
    string SubmittedOn,
    string SizeLabel);
