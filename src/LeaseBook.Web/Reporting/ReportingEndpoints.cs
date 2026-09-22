using System.Globalization;
using System.Text.Json;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Reporting.Catalog;
using LeaseBook.Modules.Reporting.Contracts;
using LeaseBook.Modules.Reporting.Rendering;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
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
/// <item><c>GET /api/statements/issued-coverage</c> — which issued statements given postings will carry forward into (#377).</item>
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

        // GET /api/reports/{id}/preview?year=&month=&ownerId=&propertyId=&bankAccountId=&asOf=&basis=
        // Returns { columns, rows, totalRows, message } — the shape the SPA's useReportPreview hook
        // expects. Columns and rows come from one typed report definition. Annotated with
        // Produces<PreviewSpaResponse> so the OpenAPI generator types the response
        // (removing the raw-fetch workaround that was needed when the response mapped to `never`).
        group.MapGet("/reports/{id}/preview",
                async (string id, int? year, int? month, Guid? ownerId, Guid? propertyId,
                    Guid? bankAccountId, DateOnly? asOf, string? basis,
                    ReportPreviewService previewService, HttpContext httpContext, CancellationToken ct) =>
                {
                    var filters = new ReportFilters(year, month, ownerId, propertyId, bankAccountId, asOf, basis);
                    var result = await previewService.PreviewAsync(id, filters, ct);
                    if (result is null)
                    {
                        return ProblemResults.Problem(
                            httpContext,
                            code: "report_not_found",
                            detail: $"Report '{id}' is not in the catalog.",
                            status: StatusCodes.Status404NotFound);
                    }

                    // result.Basis, not the bound `basis`: a report with no basis dimension echoes
                    // null, so the SPA cannot label figures with a basis the server never applied.
                    return Results.Ok(new PreviewSpaResponse(
                        result.Table.Columns, result.Table.Rows, result.Table.Rows.Count, result.Message, result.Basis));
                })
            .Produces<PreviewSpaResponse>();

        // GET /api/reports/{id}/csv?year=&month=&ownerId=&propertyId=&bankAccountId=&asOf=&basis=
        // Returns the preview rows rendered to CSV. Delegates to ReportPreviewService (same data
        // path as /preview) then serialises the row objects to a flat string table via ReportCsv.
        // `basis` binds here for the same reason it binds on /preview: this route shares the preview
        // service, so omitting it would export cash figures for a preview the user is reading on
        // accrual — the export silently disagreeing with the screen (#230).
        group.MapGet("/reports/{id}/csv",
                async (string id, int? year, int? month, Guid? ownerId, Guid? propertyId,
                    Guid? bankAccountId, DateOnly? asOf, string? basis,
                    ReportPreviewService previewService, HttpContext httpContext, CancellationToken ct) =>
                {
                    var filters = new ReportFilters(year, month, ownerId, propertyId, bankAccountId, asOf, basis);
                    var result = await previewService.PreviewAsync(id, filters, ct);
                    if (result is null)
                    {
                        return ProblemResults.Problem(
                            httpContext,
                            code: "report_not_found",
                            detail: $"Report '{id}' is not in the catalog.",
                            status: StatusCodes.Status404NotFound);
                    }

                    var descriptor = ReportCatalog.Find(id)!; // non-null: not-found returned 404 above
                    var bytes = ReportCsv.Write(
                        descriptor, result.Table.Columns, result.Table.CsvRows, result.AppliedFilters);

                    var appliedYear = result.AppliedFilters?.FirstOrDefault(filter => filter.Name == "year")?.Value;
                    var appliedMonth = result.AppliedFilters?.FirstOrDefault(filter => filter.Name == "month")?.Value;
                    var periodSuffix = appliedYear is not null && appliedMonth is not null
                        ? $"-{appliedYear}-{int.Parse(appliedMonth, CultureInfo.InvariantCulture):D2}"
                        : "";
                    var fileName = $"{id}{periodSuffix}.csv";
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
                    HttpContext httpContext, CancellationToken ct) =>
                {
                    if (from > to)
                    {
                        return ProblemResults.Problem(
                            httpContext,
                            code: "invalid_period",
                            detail: "from must be on or before to.",
                            status: StatusCodes.Status400BadRequest);
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
                        return ProblemResults.Problem(
                            httpContext,
                            code: "period_not_closed",
                            detail: $"The period {from:yyyy-MM}–{to:yyyy-MM} has a month that is not " +
                                    $"reconciliation-locked for this trust account (first open: " +
                                    $"{open.Year:D4}-{open.Month:D2}). A compliance pack requires every month " +
                                    "in the period to be closed.",
                            status: StatusCodes.Status422UnprocessableEntity);
                    }

                    CompliancePack pack;
                    try
                    {
                        pack = await assembler.AssembleAsync(bankAccountId, from, to, ct);
                    }
                    catch (KeyNotFoundException)
                    {
                        return ProblemResults.Problem(
                            httpContext,
                            code: "trust_account_not_found",
                            detail: "That trust account was not found.",
                            status: StatusCodes.Status404NotFound);
                    }

                    var company = (await branding.GetAsync(ct)).CompanyName ?? "Property Manager";
                    var generatedAt = DateTime.UtcNow;
                    var bytes = CompliancePackZip.Render(pack, company, generatedAt);

                    db.Set<AuditEvent>().Add(new AuditEvent
                    {
                        Id = UuidV7.NewId(),
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

        // GET /api/statements/issued-coverage?entryIds=&entryIds=…  |  ?runId=
        // Which already-issued owner statements the given postings will be carried forward into (#377,
        // ADR-045). A read the SPA makes after a successful post — never part of posting — so the notice
        // it drives cannot influence what was posted. Exactly one of entryIds or runId.
        group.MapGet("/statements/issued-coverage",
                async (Guid[]? entryIds, Guid? runId, ISender sender, AppDbContext db, IStatementNames names,
                    HttpContext httpContext, CancellationToken ct) =>
                {
                    var hasEntries = entryIds is { Length: > 0 };
                    if (hasEntries == runId.HasValue)
                    {
                        return ProblemResults.Problem(
                            httpContext,
                            code: "coverage_input_invalid",
                            detail: "Provide either the posted entry ids or a run id, not both.",
                            status: StatusCodes.Status400BadRequest);
                    }

                    if (hasEntries && entryIds!.Length > IssuedStatementCoverage.MaxEntryIds)
                    {
                        return ProblemResults.Problem(
                            httpContext,
                            code: "coverage_too_many_entries",
                            detail: $"Ask about at most {IssuedStatementCoverage.MaxEntryIds} entries at once; use the run id for a run.",
                            status: StatusCodes.Status400BadRequest);
                    }

                    var ids = hasEntries
                        ? entryIds!.Distinct().ToList()
                        : await IssuedStatementCoverage.RunEntryIdsAsync(db, runId!.Value, ct);

                    return Results.Ok(await IssuedStatementCoverage.ReadAsync(ids, sender, db, names, ct));
                })
            .Produces<IssuedStatementCoverageResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // POST /api/statements/{ownerId}/deliver?propertyId=&year=&month=&basis=
        // Issues the statement: renders the PDF, stores the immutable artifact, opens the first
        // delivery attempt, and records that attempt's Queued event (ADR-040). The provider send and
        // the accepted/delivered/bounced events that follow it are Track B. Returns 409 when the
        // statement's fiduciary tie-out is not balanced (StatementNotBalancedException), or when the
        // owner has no address on file. The recipient is always that address: the request names
        // none, so an owner's statement cannot be redirected, and no address travels in a URL.
        group.MapPost("/statements/{ownerId:guid}/deliver",
                async (Guid ownerId, Guid? propertyId, int? year, int? month, string? basis,
                    StatementAssembler assembler, IStatementDelivery delivery, ISender sender,
                    HttpContext httpContext, CancellationToken ct) =>
                {
                    var addresses = await sender.Query(new GetOwnerDeliveryAddresses([ownerId]), ct);
                    if (!addresses.TryGetValue(ownerId, out var toEmail))
                    {
                        return ProblemResults.Problem(
                            httpContext,
                            code: "owner_email_missing",
                            detail: "This owner has no email address on file. Add one to the owner's record, then deliver the statement.",
                            status: StatusCodes.Status409Conflict);
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
                        return ProblemResults.Problem(
                            httpContext,
                            code: "statement_not_balanced",
                            detail: ex.Message,
                            status: StatusCodes.Status409Conflict,
                            extensions: new Dictionary<string, object?>
                            {
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

}
