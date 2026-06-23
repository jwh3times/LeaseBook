using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Operations.Runs;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Operations;

/// <summary>
/// M6 operations (bulk-run) endpoints (§M6 / ADR-019). Host-owned because the
/// <see cref="RunEngine"/> is scoped to the module but the endpoint wires the HTTP surface.
/// All <c>RequirePMStaff</c>, thin (bind → dispatch → <see cref="TypedResults"/>).
/// <list type="bullet">
/// <item><c>GET /api/operations/runs/{type}/preview?year=&amp;month=</c> — preview what a run would post.</item>
/// <item><c>POST /api/operations/runs/{type}/confirm</c> — confirm the run with selected target ids.</item>
/// <item><c>GET /api/operations/runs</c> — run history (all types, most-recent-first).</item>
/// <item><c>GET /api/operations/runs/{id}</c> — single run header + items.</item>
/// </list>
/// <para>
/// <b>SPA-shaped response records (M5 lesson):</b> internal <see cref="RunPreview"/> and
/// <see cref="RunResult"/> are projected to <see cref="RunPreviewSpaResponse"/> and
/// <see cref="RunResultSpaResponse"/> whose shapes match byte-for-byte what the SPA hook types
/// expect. Returning the internal types directly caused a <c>.map of undefined</c> crash in M5.
/// </para>
/// <para>
/// <b>Transaction model:</b> preview and run-history reads are non-mutating; confirm runs inside
/// the ambient org-scoped transaction opened by <see cref="LeaseBook.Web.Tenancy.OrgContextMiddleware"/>.
/// The <see cref="RunEngine"/> documents this requirement — no nested transaction is needed here.
/// </para>
/// </summary>
public sealed class OperationsEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operations")
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Operations");

        // GET /api/operations/runs/{type}/preview?year=&month=
        // Returns the SPA-shaped preview: rows with label/amount/status + exceptions list.
        group.MapGet("/runs/{type}/preview",
                async (string type, int? year, int? month, RunEngine engine, CancellationToken ct) =>
                {
                    if (!TryParseRunType(type, out var runType))
                    {
                        return Results.Problem(
                            detail: $"Unknown run type '{type}'. Valid values: rent, latefee, disbursement.",
                            statusCode: StatusCodes.Status400BadRequest,
                            title: "unknown_run_type");
                    }

                    var now = DateTime.UtcNow;
                    var period = new RunPeriod(year ?? now.Year, month ?? now.Month);
                    var preview = await engine.PreviewAsync(runType, period, ct);

                    var rows = preview.Rows.Select(r => new PreviewRowSpa(
                        r.TargetId,
                        r.TargetKind.ToString(),
                        r.Label,
                        r.Amount,
                        r.AlreadyDone,
                        r.ExcludedReason,
                        r.Detail)).ToList();

                    return Results.Ok(new RunPreviewSpaResponse(
                        runType.ToString(),
                        period.Year,
                        period.Month,
                        rows,
                        preview.Exceptions));
                })
            .Produces<RunPreviewSpaResponse>();

        // POST /api/operations/runs/{type}/confirm
        // Body: { year, month, selectedTargetIds }
        group.MapPost("/runs/{type}/confirm",
                async (string type, ConfirmRunRequest body, RunEngine engine, CancellationToken ct) =>
                {
                    if (!TryParseRunType(type, out var runType))
                    {
                        return Results.Problem(
                            detail: $"Unknown run type '{type}'. Valid values: rent, latefee, disbursement.",
                            statusCode: StatusCodes.Status400BadRequest,
                            title: "unknown_run_type");
                    }

                    var period = new RunPeriod(body.Year, body.Month);
                    var result = await engine.ConfirmAsync(runType, period, body.SelectedTargetIds, ct);

                    return Results.Ok(new RunResultSpaResponse(
                        result.RunId,
                        runType.ToString(),
                        period.Year,
                        period.Month,
                        result.Posted,
                        result.Skipped,
                        result.Excluded,
                        result.Total));
                })
            .Produces<RunResultSpaResponse>();

        // GET /api/operations/runs — run history, most-recent-first, scoped to the request org (RLS).
        // Operations reads its OWN bulk_runs table — within-module, allowed (ADR-007).
        group.MapGet("/runs",
            async (DbContext db, CancellationToken ct) =>
            {
                var runs = await db.Set<BulkRun>()
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => new BulkRunSpa(
                        r.Id,
                        r.RunType.ToString(),
                        r.PeriodYear,
                        r.PeriodMonth,
                        r.SummaryJson,
                        r.CreatedAt))
                    .ToListAsync(ct);

                return TypedResults.Ok(new RunHistoryResponse(runs));
            });

        // GET /api/operations/runs/{id} — single run header + its items.
        group.MapGet("/runs/{id:guid}",
                async (Guid id, DbContext db, CancellationToken ct) =>
                {
                    var run = await db.Set<BulkRun>()
                        .Where(r => r.Id == id)
                        .Select(r => new BulkRunSpa(
                            r.Id,
                            r.RunType.ToString(),
                            r.PeriodYear,
                            r.PeriodMonth,
                            r.SummaryJson,
                            r.CreatedAt))
                        .FirstOrDefaultAsync(ct);

                    if (run is null)
                    {
                        return Results.NotFound(new { error = $"Run '{id}' not found." });
                    }

                    var items = await db.Set<BulkRunItem>()
                        .Where(i => i.RunId == id)
                        .OrderBy(i => i.CreatedAt)
                        .Select(i => new BulkRunItemSpa(
                            i.Id,
                            i.TargetKind.ToString(),
                            i.TargetId,
                            i.Status.ToString(),
                            i.Amount,
                            i.CreatedAt))
                        .ToListAsync(ct);

                    return Results.Ok(new BulkRunDetailResponse(run, items));
                })
            .Produces<BulkRunDetailResponse>();
    }

    private static bool TryParseRunType(string raw, out RunType runType)
    {
        runType = raw.ToLowerInvariant() switch
        {
            "rent" => RunType.Rent,
            "latefee" => RunType.LateFee,
            "disbursement" => RunType.Disbursement,
            _ => (RunType)(-1),
        };
        return (int)runType >= 0;
    }
}

// ─── SPA-shaped request / response records ────────────────────────────────────

/// <summary>Body for POST /api/operations/runs/{type}/confirm.</summary>
public sealed record ConfirmRunRequest(
    int Year,
    int Month,
    IReadOnlyList<Guid> SelectedTargetIds);

/// <summary>One preview row as the SPA expects.</summary>
public sealed record PreviewRowSpa(
    Guid TargetId,
    string TargetKind,
    string Label,
    decimal Amount,
    bool AlreadyDone,
    string? ExcludedReason,
    IReadOnlyDictionary<string, string> Detail);

/// <summary>
/// SPA shape for GET /api/operations/runs/{type}/preview.
/// Matches the <c>RunPreviewSpaResponse</c> TypeScript type in <c>useRuns.ts</c> byte-for-byte.
/// </summary>
public sealed record RunPreviewSpaResponse(
    string RunType,
    int Year,
    int Month,
    IReadOnlyList<PreviewRowSpa> Rows,
    IReadOnlyList<string> Exceptions);

/// <summary>
/// SPA shape for POST /api/operations/runs/{type}/confirm.
/// Matches the <c>RunResultSpaResponse</c> TypeScript type in <c>useRuns.ts</c> byte-for-byte.
/// </summary>
public sealed record RunResultSpaResponse(
    Guid RunId,
    string RunType,
    int Year,
    int Month,
    int Posted,
    int Skipped,
    int Excluded,
    decimal Total);

/// <summary>One run header row as the SPA expects (excludes internal jsonb).</summary>
public sealed record BulkRunSpa(
    Guid Id,
    string RunType,
    int PeriodYear,
    int PeriodMonth,
    string SummaryJson,
    DateTime CreatedAt);

/// <summary>One run-item row as the SPA expects.</summary>
public sealed record BulkRunItemSpa(
    Guid Id,
    string TargetKind,
    Guid TargetId,
    string Status,
    decimal Amount,
    DateTime CreatedAt);

/// <summary>SPA shape for GET /api/operations/runs.</summary>
public sealed record RunHistoryResponse(IReadOnlyList<BulkRunSpa> Runs);

/// <summary>SPA shape for GET /api/operations/runs/{id}.</summary>
public sealed record BulkRunDetailResponse(BulkRunSpa Run, IReadOnlyList<BulkRunItemSpa> Items);
