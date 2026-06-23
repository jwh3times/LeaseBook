namespace LeaseBook.Web.Reporting;

/// <summary>Filter bag for report preview requests.</summary>
public sealed record ReportFilters(
    int? Year = null,
    int? Month = null,
    Guid? OwnerId = null,
    Guid? PropertyId = null,
    Guid? BankAccountId = null,
    DateOnly? AsOf = null);

/// <summary>
/// Internal result from <see cref="ReportPreviewService"/> — carries the report metadata,
/// an optional message, and the raw rows. The endpoint projects this to <see cref="PreviewSpaResponse"/>
/// before serializing so the SPA receives the <c>{ columns, rows, totalRows }</c> shape it expects.
/// </summary>
public sealed record ReportPreviewResult(
    string ReportId,
    string Name,
    string Category,
    string? Message,
    IReadOnlyList<object> Rows);

/// <summary>
/// The shape the SPA's <c>useReportPreview</c> hook and <c>ReportPreviewTable</c> expect:
/// <c>{ columns, rows, totalRows, message }</c>. The endpoint converts <see cref="ReportPreviewResult"/>
/// to this before writing the HTTP response.
/// </summary>
public sealed record PreviewSpaResponse(
    IReadOnlyList<string> Columns,
    IReadOnlyList<object> Rows,
    int TotalRows,
    string? Message);
