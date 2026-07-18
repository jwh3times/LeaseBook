using System.Globalization;
using System.Text.Json;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.Modules.Reporting.Catalog;
using LeaseBook.Modules.Reporting.Contracts;
using LeaseBook.Modules.Reporting.Rendering;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Tenancy;
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
        // Returns { columns, rows, totalRows, message } — the shape the SPA's useReportPreview hook
        // expects. Columns are derived from the first row's key names (all preview rows share the same
        // keys). Annotated with Produces<PreviewSpaResponse> so the OpenAPI generator types the response
        // (removing the raw-fetch workaround that was needed when the response mapped to `never`).
        group.MapGet("/reports/{id}/preview",
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

                    // Extract column names from the dictionary keys (all preview rows share the same schema).
                    var columns = result.Rows is [Dictionary<string, object?> first, ..]
                        ? (IReadOnlyList<string>)first.Keys.ToList()
                        : [];
                    return Results.Ok(new PreviewSpaResponse(columns, result.Rows, result.Rows.Count, result.Message));
                })
            .Produces<PreviewSpaResponse>();

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

        // GET /api/reports/compliance-pack?bankAccountId=&from=&to= — the trust compliance pack ZIP.
        // PMAdmin only (it contains the audit-log extract): the route-level RequirePMAdmin ANDs with the
        // group's RequirePMStaff to admin-only. Generating a pack is itself audit-worthy, so a
        // compliance-pack-generated event is recorded (audit-worthy but not money-touching, so it never
        // appears inside the extract). The closed-period gate below requires every in-range month to be
        // reconciliation-locked for this trust account (422 `period_not_closed`; WP-8 design gate §2).
        group.MapGet("/reports/compliance-pack",
                async (Guid bankAccountId, DateOnly from, DateOnly to,
                    CompliancePackAssembler assembler, ISender sender, IPmBranding branding, AppDbContext db,
                    IActorContext actor, CancellationToken ct) =>
                {
                    if (from > to)
                    {
                        return Results.Problem(
                            detail: "from must be on or before to.",
                            statusCode: StatusCodes.Status400BadRequest, title: "invalid_period");
                    }

                    // Closed-period gate: EVERY month the pack spans must be reconciliation-locked for
                    // this trust account. Locking only the end month leaves earlier in-range months open,
                    // where a backdated posting would still shift the pack's cumulative figures after it
                    // is generated. All months locked → the displayed period is immutable.
                    var history = await sender.Query(new GetReconciliationHistory(bankAccountId), ct);
                    var lockedMonths = history.Rows
                        .Where(r => r.Status == "finalized")
                        .Select(r => (r.Year, r.Month))
                        .ToHashSet();
                    var firstOpen = MonthsInRange(from, to)
                        .Where(m => !lockedMonths.Contains(m))
                        .Select(m => ((int Year, int Month)?)m)
                        .FirstOrDefault();
                    if (firstOpen is { } open)
                    {
                        return Results.Problem(
                            detail: $"The period {from:yyyy-MM}–{to:yyyy-MM} has a month that is not " +
                                    $"reconciliation-locked for this trust account (first open: " +
                                    $"{open.Year:D4}-{open.Month:D2}). A compliance pack requires every month " +
                                    "in the period to be closed.",
                            statusCode: StatusCodes.Status422UnprocessableEntity, title: "period_not_closed");
                    }

                    CompliancePack pack;
                    try
                    {
                        pack = await assembler.AssembleAsync(bankAccountId, from, to, ct);
                    }
                    catch (KeyNotFoundException)
                    {
                        return Results.NotFound(new { error = $"Trust account '{bankAccountId}' not found." });
                    }

                    var company = (await branding.GetAsync(ct)).CompanyName ?? "Property Manager";
                    var generatedAt = DateTime.UtcNow;
                    var bytes = CompliancePackZip.Render(pack, company, generatedAt);

                    db.Set<AuditEvent>().Add(new AuditEvent
                    {
                        Id = UuidV7.NewId(),
                        ActorUserId = actor.UserId,
                        EntityType = "compliance-pack-generated",
                        EntityId = bankAccountId,
                        Action = "insert",
                        After = JsonSerializer.Serialize(new { bankAccountId, from, to, generatedAt }),
                        OccurredAt = generatedAt,
                    });
                    await db.SaveChangesAsync(ct);

                    var fileName = $"compliance-pack-{bankAccountId:N}-{from:yyyyMMdd}-{to:yyyyMMdd}.zip";
                    return Results.File(bytes, "application/zip", fileName);
                })
            .RequireAuthorization("RequirePMAdmin");

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

    // ─── helper: enumerate the (year, month) pairs a from..to range spans (inclusive) ─────

    private static IEnumerable<(int Year, int Month)> MonthsInRange(DateOnly from, DateOnly to)
    {
        var year = from.Year;
        var month = from.Month;
        while (year < to.Year || (year == to.Year && month <= to.Month))
        {
            yield return (year, month);
            if (month == 12)
            {
                month = 1;
                year++;
            }
            else
            {
                month++;
            }
        }
    }

    // ─── helper: project preview rows (generic objects) to a string table ─────

    /// <summary>
    /// Projects the preview rows to a (columns, rows) pair suitable for <see cref="ReportCsv.Write"/>.
    /// Handles both in-process <c>Dictionary&lt;string, object?&gt;</c> rows (CSV path, where
    /// <see cref="ReportPreviewService"/> returns boxed dictionaries directly) and
    /// <c>JsonElement</c> rows (if the rows ever reach this method after a JSON round-trip).
    /// Each unique key across all rows becomes a column (first-seen order); values are coerced
    /// to invariant-culture strings — <c>decimal</c> exact, <c>DateOnly</c>/<c>DateTime</c> ISO,
    /// <c>bool</c> lowercase, <c>null</c> empty.
    /// </summary>
    private static (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
        ProjectToStringTable(IReadOnlyList<object> rows)
    {
        if (rows.Count == 0)
        {
            return ([], []);
        }

        // ── In-process path: ReportPreviewService returns Dictionary<string, object?> rows ──
        var dictRows = rows.OfType<Dictionary<string, object?>>().ToList();
        if (dictRows.Count == rows.Count)
        {
            // Collect all unique keys preserving first-seen order across all rows.
            var columns = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in dictRows)
            {
                foreach (var key in row.Keys)
                {
                    if (seen.Add(key))
                    {
                        columns.Add(key);
                    }
                }
            }

            var stringRows = dictRows.Select(row =>
                (IReadOnlyList<string>)columns.Select(col =>
                {
                    row.TryGetValue(col, out var val);
                    return CoerceObjectToString(val);
                }).ToList()
            ).ToList();

            return (columns, stringRows);
        }

        // ── JSON round-trip path: rows are JsonElement dictionaries ──
        var jsonRows = rows.OfType<System.Text.Json.JsonElement>().ToList();
        if (jsonRows.Count == 0)
        {
            return ([], []);
        }

        {
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

    /// <summary>
    /// Coerces a boxed <c>object?</c> value from an in-process preview row to an invariant-culture
    /// string. Keeps <c>decimal</c> exact (never float), formats dates as ISO, booleans lowercase.
    /// </summary>
    private static string CoerceObjectToString(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        decimal d => d.ToString("0.00########", CultureInfo.InvariantCulture),
        DateOnly dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dtm => dtm.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        Guid g => g.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
