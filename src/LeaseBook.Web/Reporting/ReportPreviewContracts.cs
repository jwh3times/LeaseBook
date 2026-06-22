namespace LeaseBook.Web.Reporting;

/// <summary>Filter bag for report preview requests.</summary>
public sealed record ReportFilters(
    int? Year = null,
    int? Month = null,
    Guid? OwnerId = null,
    Guid? PropertyId = null,
    Guid? BankAccountId = null,
    DateOnly? AsOf = null);

/// <summary>Generic preview result — rows is a list of dictionaries for flexible SPA rendering.</summary>
public sealed record ReportPreviewResult(
    string ReportId,
    string Name,
    string Category,
    string? Message,
    IReadOnlyList<object> Rows);
