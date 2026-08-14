using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Features.Reporting;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / WP-3) for the Operations module's <see cref="IDelinquencyData"/>
/// port. Dispatches two queries via <see cref="ISender"/>:
/// <list type="bullet">
///   <item>Accounting's <see cref="GetDelinquencyAging"/> — per-tenant receivable balance with real age.</item>
///   <item>Directory's <see cref="GetActiveLeaseSchedule"/> — active leases with dimension ids.</item>
///   <item>Accounting's <see cref="GetRentObligationEntries"/> — canonical, unreversed rent entries
///     that remain open under oldest-charge-first allocation at the assessment date.</item>
/// </list>
/// Joins them on <c>tenant_id</c> to produce one delinquency row for the tenant's active lease.
/// Directory's command and database constraints guarantee at most one active lease per tenant; the
/// dictionary construction deliberately fails rather than guessing if that invariant is violated.
/// </summary>
internal sealed class DelinquencyDataAdapter(ISender sender) : IDelinquencyData
{
    public async Task<IReadOnlyList<DelinquentLedgerRow>> GetAsync(
        int year, int month, DateOnly asOf, CancellationToken ct)
    {
        // Fetch delinquent tenants (positive net receivable balance) and active lease schedule sequentially.
        // Sequential is required because both queries share the ambient EF DbContext (not thread-safe).
        var aging = await sender.Query(new GetDelinquencyAging(asOf), ct);
        var schedule = await sender.Query(new GetActiveLeaseSchedule(year, month, asOf), ct);
        var rentRefs = schedule.Rows
            .Select(l => $"rent:{year}-{month:00}:lease={l.LeaseId}")
            .ToList();
        var rentObligations = rentRefs.Count == 0
            ? new RentObligationEntriesResponse([])
            : await sender.Query(new GetRentObligationEntries(rentRefs, asOf), ct);
        var rentObligationByRef = rentObligations.Rows.ToDictionary(r => r.SourceRef, StringComparer.Ordinal);

        // Index delinquency rows by tenant_id. Only tenants with Total > 0 are present
        // (HAVING SUM > 0 in GetDelinquencyAging).
        var agingByTenant = aging.Rows
            .Where(r => r.Total > 0m)
            .ToDictionary(r => r.TenantId);

        var leaseByTenant = schedule.Rows.ToDictionary(l => l.TenantId);

        var result = new List<DelinquentLedgerRow>();
        foreach (var (tenantId, agingRow) in agingByTenant)
        {
            if (!leaseByTenant.TryGetValue(tenantId, out var lease))
            {
                continue; // Delinquent tenant has no active lease in this period — skip.
            }

            var rentRef = $"rent:{year}-{month:00}:lease={lease.LeaseId}";
            var attribution = rentObligationByRef.TryGetValue(rentRef, out var obligation) &&
                obligation.TenantId == lease.TenantId
                ? (DelinquencyAttribution)new DelinquencyAttribution.AttributedToLease(obligation.EntryId)
                : new DelinquencyAttribution.NoRentObligation();
            result.Add(new DelinquentLedgerRow(
                LeaseId: lease.LeaseId,
                TenantId: lease.TenantId,
                PropertyId: lease.PropertyId,
                OwnerId: lease.OwnerId,
                UnitId: lease.UnitId,
                TenantName: lease.TenantName,
                UnitLabel: lease.UnitLabel,
                Rent: lease.Rent,
                Balance: agingRow.Total,
                Attribution: attribution));
        }

        return result;
    }
}
