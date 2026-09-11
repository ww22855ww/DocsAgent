namespace MockPortal.Models;

/// <summary>One row in the document query result list.</summary>
public sealed record DocumentRecord(
    string FileName,
    string DocumentNo,
    string Department,
    string DocumentType,
    string Vendor,
    string IssuedOn,
    string SizeLabel);
