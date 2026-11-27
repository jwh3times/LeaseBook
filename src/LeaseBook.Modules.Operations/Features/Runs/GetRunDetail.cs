using System.Text.Json;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Operations.Runs;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Operations.Features.Runs;

/// <summary>
/// What one confirmed run did to each target it touched (#573): every item's run outcome, its reason
/// when it did not post, and who the target is today. <c>null</c> when no run with this id is visible
/// to the ambient organization (RLS), which the endpoint reports as not found.
/// </summary>
public sealed record GetRunDetail(Guid RunId) : IQuery<RunDetail?>;

/// <summary>A run's header and every item it recorded, in <see cref="GetRunDetailHandler"/>'s order.</summary>
public sealed record RunDetail(
    Guid Id,
    RunType RunType,
    int PeriodYear,
    int PeriodMonth,
    string SummaryJson,
    DateTime CreatedAt,
    IReadOnlyList<RunDetailItem> Items);

/// <summary>
/// One target's run outcome.
/// </summary>
/// <param name="Reason">Present exactly when <paramref name="Outcome"/> is skipped or excluded.</param>
/// <param name="TargetName">
/// The tenant's name for a lease, the owner's for an owner; <c>null</c> when the target is not found.
/// </param>
/// <param name="UnitLabel">The lease's unit; <c>null</c> for an owner or a lease not found.</param>
/// <param name="TenantId">The lease's tenant, for a link to the ledger; <c>null</c> otherwise.</param>
public sealed record RunDetailItem(
    Guid Id,
    RunTargetKind TargetKind,
    Guid TargetId,
    RunItemStatus Outcome,
    decimal Amount,
    RunReason? Reason,
    string? TargetName,
    string? UnitLabel,
    Guid? TenantId);

internal sealed class GetRunDetailHandler(DbContext db, IRunTargetNames names)
    : IQueryHandler<GetRunDetail, RunDetail?>
{
    public async Task<RunDetail?> Handle(GetRunDetail query, CancellationToken ct)
    {
        // Operations reads its own run tables (ADR-007), on the ambient org-scoped transaction.
        var run = await db.Set<BulkRun>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == query.RunId, ct);
        if (run is null)
        {
            return null;
        }

        var stored = await db.Set<BulkRunItem>().AsNoTracking()
            .Where(i => i.RunId == run.Id)
            .Select(i => new { i.Id, i.TargetKind, i.TargetId, i.Status, i.Amount, i.SnapshotJson })
            .ToListAsync(ct);

        var leases = await BatchRead.MapOrEmptyAsync(
            TargetIds(RunTargetKind.Lease), ids => names.GetLeaseNamesAsync(ids, ct));
        var owners = await BatchRead.MapOrEmptyAsync(
            TargetIds(RunTargetKind.Owner), ids => names.GetOwnerNamesAsync(ids, ct));

        var items = stored
            .Select(i =>
            {
                var lease = i.TargetKind == RunTargetKind.Lease ? leases.GetValueOrDefault(i.TargetId) : null;
                var name = i.TargetKind == RunTargetKind.Lease
                    ? lease?.TenantName
                    : owners.GetValueOrDefault(i.TargetId);
                return new RunDetailItem(
                    i.Id, i.TargetKind, i.TargetId, i.Status, i.Amount,
                    ReasonOf(i.Id, i.Status, i.SnapshotJson), name, lease?.UnitLabel, lease?.TenantId);
            })
            .OrderBy(i => OutcomeOrder(i.Outcome))
            .ThenBy(i => i.TargetName is null)
            .ThenBy(i => i.TargetName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Id)
            .ToList();

        return new RunDetail(
            run.Id, run.RunType, run.PeriodYear, run.PeriodMonth, run.SummaryJson, run.CreatedAt, items);

        List<Guid> TargetIds(RunTargetKind kind) =>
            stored.Where(i => i.TargetKind == kind).Select(i => i.TargetId).Distinct().ToList();
    }

    /// <summary>
    /// What needs attention first: an exclusion (the work could not be done), then a skip (it was
    /// already done), then what posted.
    /// </summary>
    private static int OutcomeOrder(RunItemStatus outcome) => outcome switch
    {
        RunItemStatus.Excluded => 0,
        RunItemStatus.Skipped => 1,
        RunItemStatus.Posted => 2,
        _ => throw new InvalidOperationException($"No display order for run outcome {outcome}."),
    };

    /// <summary>
    /// The reason the run engine stored under <c>reason</c>. A skipped or excluded item without one,
    /// or a posted item with one, contradicts how every run since M6 has been recorded, so it is
    /// refused rather than shown as something the run never said.
    /// </summary>
    private static RunReason? ReasonOf(Guid itemId, RunItemStatus outcome, string snapshotJson)
    {
        using var snapshot = JsonDocument.Parse(snapshotJson);
        var code = snapshot.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null;

        return (outcome, code) switch
        {
            (RunItemStatus.Posted, null) => null,
            (not RunItemStatus.Posted, not null) => RunReasons.FromCode(code),
            _ => throw new InvalidOperationException(
                $"Run item {itemId} is {outcome} but its snapshot {(code is null ? "has no" : "has a")} reason."),
        };
    }
}
