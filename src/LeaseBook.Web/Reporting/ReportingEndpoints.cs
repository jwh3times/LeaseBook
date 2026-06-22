using System.Globalization;
using LeaseBook.Modules.Reporting.Catalog;
using LeaseBook.Modules.Reporting.Rendering;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// M5 reporting endpoints (§M5 / ADR-016). Host-owned because the statement assembler and
/// preview service cross module boundaries via <see cref="LeaseBook.SharedKernel.Cqrs.ISender"/>
/// (the legitimate composition root). All <c>RequirePMStaff</c>, thin (bind → dispatch →
/// <c>TypedResults</c>).
/// <list type="bullet">
/// <item><c>GET /api/reports</c> — full catalog.</item>
/// <item><c>GET /api/reports/{id}/preview</c> — run/preview → generic rows.</item>
/// <item><c>GET /api/reports/{id}/csv</c> — generic report as CSV download.</item>
/// <item><c>GET /api/statements/{ownerId}</c> — assembled <see cref="StatementView"/> JSON.</item>
/// <item><c>GET /api/statements/{ownerId}/pdf</c> — statement as PDF download.</item>
/// <item><c>GET /api/statements/{ownerId}/csv</c> — statement as CSV download.</item>
/// <item><c>POST /api/statements/{ownerId}/deliver</c> — render PDF, store artifact, queue delivery.</item>
/// </list>
/// </summary>
public sealed class ReportingEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api")
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Reporting");

        // GET /api/reports — return the full static catalog.
        group.MapGet("/reports", () =>
                TypedResults.Ok(ReportCatalog.All))
            .Produces<IReadOnlyList<ReportDescriptor>>();

        // GET /api/reports/{id}/preview?year=&month=&ownerId=&propertyId=&bankAccountId=&asOf=
        group.MapGet("/reports/{id}/preview",
                async (string id, int? year, int? month, Guid? ownerId, Guid? propertyId,
                    Guid? bankAccountId, DateOnly? asOf,
                    ReportPreviewService previewService, CancellationToken ct) =>
                {
                    var filters = new ReportFilters(year, month, ownerId, propertyId, bankAccountId, asOf);
                    var result = await previewService.PreviewAsync(id, filters, ct);
                    return result is null
                        ? Results.NotFound(new { error = $"Report '{id}' not found in catalog." })
                        : Results.Ok(result);
                });

        // GET /api/reports/{id}/csv?year=&month=&ownerId=&propertyId=&bankAccountId=&asOf=
        // Returns the preview rows rendered to CSV. Delegates to ReportPreviewService (same data
        // path as /preview) then serialises the row objects to a flat string table via ReportCsv.
        group.MapGet("/reports/{id}/csv",
                async (string id, int? year, int? month, Guid? ownerId, Guid? propertyId,
                    Guid? bankAccountId, DateOnly? asOf,
                    ReportPreviewService previewService, CancellationToken ct) =>
                {
                    var filters = new ReportFilters(year, month, ownerId, propertyId, bankAccountId, asOf);
                    var result = await previewService.PreviewAsync(id, filters, ct);
                    if (result is null)
                    {
                        return Results.NotFound(new { error = $"Report '{id}' not found in catalog." });
                    }

                    // The preview rows are generic objects; project them to string rows for the CSV
                    // renderer (which is column-agnostic). We collect distinct key names as columns
                    // then format each row's values in that order (empty string when key absent).
                    var descriptor = ReportCatalog.Find(id)!; // non-null: not-found returned 404 above
                    var (columns, stringRows) = ProjectToStringTable(result.Rows);
                    var bytes = ReportCsv.Write(descriptor, columns, stringRows);

                    var fileName = $"{id}-{(year ?? DateTime.UtcNow.Year)}-{(month ?? DateTime.UtcNow.Month):D2}.csv";
                    return Results.File(bytes, "text/csv", fileName);
                });

        // GET /api/statements/{ownerId}?propertyId=&year=&month=&basis=
        // Always returns 200 with zero figures for an owner with no journal activity in the period
        // (the owner is valid, just quiet). The fiduciary panel's Balanced flag confirms correctness.
        group.MapGet("/statements/{ownerId:guid}",
                async (Guid ownerId, Guid? propertyId, int? year, int? month, string? basis,
                    StatementAssembler assembler, CancellationToken ct) =>
                {
                    var now = DateTime.UtcNow;
                    var resolvedYear = year ?? now.Year;
                    var resolvedMonth = month ?? now.Month;
                    var resolvedBasis = basis?.ToLowerInvariant() is "accrual" ? "accrual" : "cash";

                    var views = await assembler.BuildAsync(
                        [ownerId], propertyId, resolvedYear, resolvedMonth, resolvedBasis, ct);

                    // BuildAsync returns one view per owner. An owner with no journal activity
                    // produces a zeroed statement — never an empty list for a single ownerId.
                    return TypedResults.Ok(views[0]);
                });

        // GET /api/statements/{ownerId}/pdf?propertyId=&year=&month=&basis=
        group.MapGet("/statements/{ownerId:guid}/pdf",
                async (Guid ownerId, Guid? propertyId, int? year, int? month, string? basis,
                    StatementAssembler assembler, CancellationToken ct) =>
                {
                    var now = DateTime.UtcNow;
                    var resolvedYear = year ?? now.Year;
                    var resolvedMonth = month ?? now.Month;
                    var resolvedBasis = basis?.ToLowerInvariant() is "accrual" ? "accrual" : "cash";

                    var views = await assembler.BuildAsync(
                        [ownerId], propertyId, resolvedYear, resolvedMonth, resolvedBasis, ct);

                    var bytes = StatementPdf.Render(views[0]);
                    var fileName = $"statement-{ownerId:N}-{resolvedYear}-{resolvedMonth:D2}-{resolvedBasis}.pdf";
                    return Results.File(bytes, "application/pdf", fileName);
                });

        // GET /api/statements/{ownerId}/csv?propertyId=&year=&month=&basis=
        group.MapGet("/statements/{ownerId:guid}/csv",
                async (Guid ownerId, Guid? propertyId, int? year, int? month, string? basis,
                    StatementAssembler assembler, CancellationToken ct) =>
                {
                    var now = DateTime.UtcNow;
                    var resolvedYear = year ?? now.Year;
                    var resolvedMonth = month ?? now.Month;
                    var resolvedBasis = basis?.ToLowerInvariant() is "accrual" ? "accrual" : "cash";

                    var views = await assembler.BuildAsync(
                        [ownerId], propertyId, resolvedYear, resolvedMonth, resolvedBasis, ct);

                    var bytes = StatementCsv.Write(views[0]);
                    var fileName = $"statement-{ownerId:N}-{resolvedYear}-{resolvedMonth:D2}-{resolvedBasis}.csv";
                    return Results.File(bytes, "text/csv", fileName);
                });

        // POST /api/statements/{ownerId}/deliver?propertyId=&year=&month=&basis=&toEmail=
        // Renders the statement PDF, stores an immutable artifact, and records a DeliveryRecord
        // with state Queued. The actual ACS email send is deferred to M8. Returns 409 when the
        // statement's fiduciary tie-out is not balanced (StatementNotBalancedException).
        group.MapPost("/statements/{ownerId:guid}/deliver",
                async (Guid ownerId, Guid? propertyId, int? year, int? month, string? basis,
                    string? toEmail,
                    StatementAssembler assembler, IStatementDelivery delivery,
                    CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(toEmail))
                    {
                        return Results.Problem(
                            detail: "toEmail is required.",
                            statusCode: StatusCodes.Status400BadRequest,
                            title: "missing_to_email");
                    }

                    var now = DateTime.UtcNow;
                    var resolvedYear = year ?? now.Year;
                    var resolvedMonth = month ?? now.Month;
                    var resolvedBasis = basis?.ToLowerInvariant() is "accrual" ? "accrual" : "cash";

                    var views = await assembler.BuildAsync(
                        [ownerId], propertyId, resolvedYear, resolvedMonth, resolvedBasis, ct);

                    try
                    {
                        var result = await delivery.DeliverAsync(views[0], toEmail, ct);
                        return TypedResults.Ok(result);
                    }
                    catch (StatementNotBalancedException ex)
                    {
                        return Results.Problem(
                            detail: ex.Message,
                            statusCode: StatusCodes.Status409Conflict,
                            title: "statement_not_balanced",
                            extensions: new Dictionary<string, object?>
                            {
                                ["ownerId"] = ex.OwnerId,
                                ["year"] = ex.Year,
                                ["month"] = ex.Month,
                                ["variance"] = ex.Variance,
                            });
                    }
                });
    }

    // ─── helper: project preview rows (generic objects) to a string table ─────

    /// <summary>
    /// Projects the preview rows (generic <c>object</c> elements, which arrive as
    /// <c>JsonElement</c> dictionaries after JSON round-trip) to a (columns, rows) pair suitable
    /// for <see cref="ReportCsv.Write"/>. Each unique key across all rows becomes a column; values
    /// are coerced to invariant strings. Empty string when a row lacks a key.
    /// </summary>
    private static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
        ProjectToStringTable(IReadOnlyList<object> rows)
    {
        var jsonRows = rows.OfType<System.Text.Json.JsonElement>().ToList();
        if (jsonRows.Count == 0)
        {
            return ([], []);
        }

        // Collect all unique keys preserving first-seen order
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in jsonRows)
        {
            foreach (var prop in row.EnumerateObject())
            {
                if (seen.Add(prop.Name))
                {
                    columns.Add(prop.Name);
                }
            }
        }

        var stringRows = jsonRows.Select(row =>
        {
            return (IReadOnlyList<string>)columns.Select(col =>
            {
                if (row.TryGetProperty(col, out var el))
                {
                    return el.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.Number =>
                            el.TryGetDecimal(out var d)
                                ? d.ToString("0.00########", CultureInfo.InvariantCulture)
                                : el.GetRawText(),
                        System.Text.Json.JsonValueKind.True => "true",
                        System.Text.Json.JsonValueKind.False => "false",
                        System.Text.Json.JsonValueKind.Null => string.Empty,
                        _ => el.GetString() ?? string.Empty,
                    };
                }

                return string.Empty;
            }).ToList();
        }).ToList();

        return (columns, stringRows);
    }
}
