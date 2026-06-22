using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Reporting.Contracts;

namespace LeaseBook.Web.Reporting;

/// <summary>
/// Composes <see cref="StatementView"/> instances for a batch of owners (ADR-016). Lives in the
/// host because it crosses the Accounting→Reporting module boundary: it calls
/// <see cref="IOwnerStatementData"/> (Accounting port) and enriches each statement with display
/// names (via <see cref="IStatementNames"/>), PM branding (via <see cref="IPmBranding"/>), and the
/// latest finalized bank reconciliation snapshot (via <see cref="IReconciliationSnapshots"/>).
/// <b>No financial math here</b> — every figure is verbatim from the Accounting engine.
/// </summary>
public sealed class StatementAssembler(
    IOwnerStatementData statementData,
    IStatementNames names,
    IPmBranding branding,
    IReconciliationSnapshots reconciliationSnapshots)
{
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
        var byOwner = await statementData.GetAsync(ownerIds, propertyId, year, month, basis, ct);
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

            views.Add(new StatementView(
                ownerId, ownerName, propertyAddress, stmt.Basis, stmt.Year, stmt.Month,
                stmt.Beginning, sectionViews, stmt.Ending, fiduciary, pmBranding));
        }

        return views;
    }
}
