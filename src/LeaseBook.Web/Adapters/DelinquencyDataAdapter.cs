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
/// </list>
/// Joins them on <c>tenant_id</c> to produce per-lease delinquency rows with rules:
/// <list type="bullet">
///   <item>A tenant with exactly one active lease → one chargeable row attributed to that lease.</item>
///   <item>A tenant with MORE than one active lease → excluded with reason
///     <c>"ambiguous_multiple_active_leases"</c>; no fee is posted to any of their leases
///     (Phase 1: can't attribute a tenant-level balance to one lease without per-lease GL).</item>
/// </list>
/// </summary>
internal sealed class DelinquencyDataAdapter(ISender sender) : IDelinquencyData
{
    public async Task<IReadOnlyList<DelinquentLedgerRow>> GetAsync(
        int year, int month, DateOnly asOf, CancellationToken ct)
    {
        // Fetch delinquent tenants (positive net receivable balance) and active lease schedule sequentially.
        // Sequential is required because both queries share the ambient EF DbContext (not thread-safe).
        var aging = await sender.Query(new GetDelinquencyAging(asOf), ct);
        var schedule = await sender.Query(new GetActiveLeaseSchedule(year, month), ct);

        // Index delinquency rows by tenant_id. Only tenants with Total > 0 are present
        // (HAVING SUM > 0 in GetDelinquencyAging).
        var agingByTenant = aging.Rows
            .Where(r => r.Total > 0m)
            .ToDictionary(r => r.TenantId);

        // Group leases by tenant_id so we can detect the multi-lease ambiguity case.
        var leasesByTenant = schedule.Rows
            .GroupBy(l => l.TenantId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<DelinquentLedgerRow>();
        foreach (var (tenantId, agingRow) in agingByTenant)
        {
            if (!leasesByTenant.TryGetValue(tenantId, out var tenantLeases))
            {
                continue; // Delinquent tenant has no active lease in this period — skip.
            }

            if (tenantLeases.Count > 1)
            {
                // Cannot attribute a tenant-level balance to one lease when multiple are active.
                // Surface one excluded row per lease so the preview can list them with the reason.
                foreach (var lease in tenantLeases)
                {
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
                        DaysLate: -1)); // Sentinel: signals ambiguous_multiple_active_leases to strategy.
                }
                continue;
            }

            // Exactly one active lease — attribute the balance to it.
            var singleLease = tenantLeases[0];
            result.Add(new DelinquentLedgerRow(
                LeaseId: singleLease.LeaseId,
                TenantId: singleLease.TenantId,
                PropertyId: singleLease.PropertyId,
                OwnerId: singleLease.OwnerId,
                UnitId: singleLease.UnitId,
                TenantName: singleLease.TenantName,
                UnitLabel: singleLease.UnitLabel,
                Rent: singleLease.Rent,
                Balance: agingRow.Total,
                DaysLate: agingRow.OldestAgeDays)); // Real age from the Accounting query.
        }

        return result;
    }
}
