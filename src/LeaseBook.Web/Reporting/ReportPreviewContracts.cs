namespace LeaseBook.Web.Reporting;

/// <summary>Filter bag for report preview requests.</summary>
public sealed record ReportFilters(
    int? Year = null,
    int? Month = null,
    Guid? OwnerId = null,
    Guid? PropertyId = null,
    Guid? BankAccountId = null,
    DateOnly? AsOf = null,
    string? Basis = null);

/// <summary>
/// Internal result from <see cref="ReportPreviewService"/> — carries the report metadata,
/// an optional message, and the raw rows. The endpoint projects this to <see cref="PreviewSpaResponse"/>
/// before serializing so the SPA receives the <c>{ columns, rows, totalRows }</c> shape it expects.
/// </summary>
/// <param name="Basis">
/// The basis the figures were actually computed on, echoed back so the SPA labels what the server
/// did rather than what the client asked for — the same posture as <c>StatementView.Basis</c>.
/// <c>null</c> means this report has no basis dimension, which is the normal case: only
/// <c>owner-bal</c> reads an account class that carries single-basis lines. A report that renders a
/// basis label from local state instead of this value is the defect #229 removed.
/// </param>
public sealed record ReportPreviewResult(
    string ReportId,
    string Name,
    string Category,
    string? Message,
    IReadOnlyList<object> Rows,
    string? Basis = null);

/// <summary>
/// The shape the SPA's <c>useReportPreview</c> hook and <c>ReportPreviewTable</c> expect:
/// <c>{ columns, rows, totalRows, message }</c>. The endpoint converts <see cref="ReportPreviewResult"/>
/// to this before writing the HTTP response.
/// </summary>
public sealed record PreviewSpaResponse(
    IReadOnlyList<string> Columns,
    IReadOnlyList<object> Rows,
    int TotalRows,
    string? Message,
    string? Basis = null);
