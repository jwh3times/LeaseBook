using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Statements;
using LeaseBook.Modules.Reporting.Contracts;
using LeaseBook.Modules.Reporting.Delivery;
using LeaseBook.Web.Observability;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// Composes <see cref="StatementView"/> instances for a batch of owners (ADR-016). Lives in the
/// host because it crosses the Accounting→Reporting module boundary: it calls
/// <see cref="IOwnerStatementData"/> (Accounting port) and enriches each statement with display
/// names (via <see cref="IStatementNames"/>), PM branding (via <see cref="IPmBranding"/>), and the
/// latest finalized bank reconciliation snapshot (via <see cref="IReconciliationSnapshots"/>).
/// <para>
/// It also resolves each owner's <b>anchor</b> — the statement already issued for the preceding period
/// with the same basis and scope — from the append-only artifact history, and hands it to Accounting
/// so the statement carries forward from what the owner was actually given (ADR-045).
/// </para>
/// <b>No financial math here</b> — every figure is verbatim from the Accounting engine.
/// </summary>
public sealed class StatementAssembler(
    IOwnerStatementData statementData,
    IStatementNames names,
    IPmBranding branding,
    IReconciliationSnapshots reconciliationSnapshots,
    AppDbContext db,
    ILogger<StatementAssembler> logger)
{
    private sealed record IssuedAnchor(
        Guid OwnerId, int PeriodYear, int PeriodMonth, string Basis, Guid? PropertyId,
        DateTime CreatedAt, decimal EndingBalance, DateTime AsOf);

    /// <summary>
    /// Builds a statement view for each owner in <paramref name="ownerIds"/>.
    /// Always returns one view per owner — zeroed (beginning = 0, no sections, ending = 0) when
    /// there is no journal activity for the period, never absent.
    /// </summary>
    public async Task<IReadOnlyList<StatementView>> BuildAsync(
        IReadOnlyList<Guid> ownerIds,
        Guid? propertyId,
        int year,
        int month,
        string basis,
        CancellationToken ct)
    {
        // Sequential reads — EF Core DbContext is not thread-safe; parallel awaits on the same
        // scoped context would race. Each call dispatches through ISender which uses the ambient
        // request-scoped DbContext, so they must run serially.
        var prior = new DateOnly(year, month, 1).AddMonths(-1);
        var anchors = await IssuedAnchorsAsync(ownerIds, propertyId, prior, basis, ct);

        var byOwner = await statementData.GetAsync(
            ownerIds, propertyId, year, month, basis,
            anchors.ToDictionary(a => a.Key, a => new StatementAnchor(a.Value.EndingBalance, a.Value.AsOf)),
            ct);
        var ownerNames = await names.GetOwnerNamesAsync(ct);
        var propertyAddresses = await names.GetPropertyAddressesAsync(ct);
        var pmBranding = await branding.GetAsync(ct);
        var snapshots = await reconciliationSnapshots.GetLatestFinalizedAsync(ct);

        // Latest reconciliation across all bank accounts (statement header needs one figure).
        var latestSnapshot = snapshots.Values
            .OrderByDescending(s => s.FinalizedAt)
            .FirstOrDefault();

        var views = new List<StatementView>(ownerIds.Count);

        foreach (var ownerId in ownerIds)
        {
            // GetOwnerStatementDataHandler always inserts an entry for every requested owner
            // (zeroed when there is no journal activity), so TryGetValue never misses.
            var stmt = byOwner[ownerId];

            var ownerName = ownerNames.GetValueOrDefault(ownerId, "Unknown");
            var propertyAddress = propertyId.HasValue
                ? propertyAddresses.GetValueOrDefault(propertyId.Value)
                : null;

            var sectionViews = stmt.Sections
                .Select(s => new StatementSectionView(
                    s.Key.ToString(),
                    s.Title,
                    s.Lines.Select(l => new StatementLineView(
                        l.EntryId, l.Date, l.EventType, l.EventSubtype, l.Description,
                        l.PropertyId.HasValue ? propertyAddresses.GetValueOrDefault(l.PropertyId.Value) : null,
                        l.Amount)).ToList(),
                    s.Subtotal))
                .ToList();

            var fiduciary = new FiduciaryPanel(
                stmt.TieOut.Balanced,
                stmt.TieOut.Variance,
                stmt.TieOut.PmIncomeExcluded,
                stmt.TieOut.DepositsRecognizedOnApplication,
                latestSnapshot);

            CarryForwardView? carryForward = null;

            // Rendered only when there is something to show: nothing changed since the anchor was issued
            // means the owner's two documents already chain, and the section would be noise.
            if (stmt.Adjustments is { } adj && (adj.Total != 0m || adj.Unitemized != 0m))
            {
                carryForward = new CarryForwardView(
                    prior.Year,
                    prior.Month,
                    anchors[ownerId].CreatedAt,
                    adj.IssuedEnding,
                    adj.Lines.Select(l => new CarryForwardLineView(
                        l.EntryId, l.Date, l.PostedAt, l.EventType, l.Description,
                        l.PropertyId.HasValue ? propertyAddresses.GetValueOrDefault(l.PropertyId.Value) : null,
                        l.Amount)).ToList(),
                    adj.Unitemized,
                    adj.Total);

                if (adj.Unitemized != 0m)
                {
                    // Identified by the stored anchor artifact, not the request: the anchor matched this
                    // owner, period, basis and scope exactly, and its row is data the organization issued
                    // rather than caller-supplied text (CWE-117 log forging).
                    var anchor = anchors[ownerId];
                    logger.LogWarning(
                        LogEvents.StatementCarryForwardUnitemized,
                        "Statement carry-forward has {Unitemized:0.00} of {Total:0.00} not attributable to an " +
                        "entry posted after the issued {PriorYear}-{PriorMonth:D2} statement for owner {OwnerId} " +
                        "({Basis}, property {PropertyId}). Rendered as an unitemized adjustment.",
                        adj.Unitemized, adj.Total, anchor.PeriodYear, anchor.PeriodMonth, anchor.OwnerId,
                        anchor.Basis, anchor.PropertyId);
                }
            }

            views.Add(new StatementView(
                ownerId, ownerName, propertyId, propertyAddress, stmt.Basis, stmt.Year, stmt.Month,
                stmt.Beginning, carryForward, sectionViews, stmt.Ending, fiduciary, pmBranding, stmt.AsOf));
        }

        return views;
    }

    /// <summary>
    /// The newest issued statement per owner for exactly the <paramref name="prior"/> month, with the same
    /// basis and property scope, that recorded its figures. Artifacts issued before ADR-045 have no
    /// recorded figures and never anchor; a skipped month is never bridged.
    /// </summary>
    private async Task<Dictionary<Guid, IssuedAnchor>> IssuedAnchorsAsync(
        IReadOnlyList<Guid> ownerIds, Guid? propertyId, DateOnly prior, string basis, CancellationToken ct)
    {
        var issued = await db.Set<StatementArtifact>()
            .Where(a => ownerIds.Contains(a.OwnerId)
                && a.PeriodYear == prior.Year
                && a.PeriodMonth == prior.Month
                && a.Basis == basis
                && a.PropertyId == propertyId
                && a.EndingBalance != null
                && a.AsOf != null)
            .Select(a => new { a.OwnerId, a.PeriodYear, a.PeriodMonth, a.Basis, a.PropertyId, a.CreatedAt, a.EndingBalance, a.AsOf })
            .ToListAsync(ct);

        return issued
            .GroupBy(a => a.OwnerId)
            .Select(g => g.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.AsOf).First())
            .ToDictionary(
                a => a.OwnerId,
                a => new IssuedAnchor(
                    a.OwnerId, a.PeriodYear, a.PeriodMonth, a.Basis!, a.PropertyId, a.CreatedAt,
                    a.EndingBalance!.Value, DateTime.SpecifyKind(a.AsOf!.Value, DateTimeKind.Utc)));
    }
}
