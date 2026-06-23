using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Features.Reporting;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / WP-3) for the Operations module's <see cref="IDelinquencyData"/>
/// port. Dispatches two queries via <see cref="ISender"/>:
/// <list type="bullet">
///   <item>Accounting's <see cref="GetDelinquencyAging"/> — per-tenant receivable balance with age buckets.</item>
///   <item>Directory's <see cref="GetActiveLeaseSchedule"/> — active leases with dimension ids.</item>
/// </list>
/// Joins them on <c>tenant_id</c> to produce per-lease delinquency rows. A tenant with multiple
/// active leases gets one row per lease (Phase 1 simplification; balance is the tenant total).
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

        // Join lease schedule rows to aging; produce one row per (delinquent tenant, lease).
        var result = new List<DelinquentLedgerRow>();
        foreach (var lease in schedule.Rows)
        {
            if (!agingByTenant.TryGetValue(lease.TenantId, out var agingRow))
            {
                continue; // Tenant not delinquent — skip.
            }

            // Days late: oldest past-due bucket determines the delinquency age.
            // Use D1_30, D31_60, D61_90, Over90 in priority order. If any bucket > 0, we count
            // the midpoint of the oldest occupied bucket as "days late" for preview display.
            // The strategy only cares that days_late > 0 (i.e., past the grace period); the exact
            // number is informational in the preview.
            var daysLate = ComputeDaysLate(agingRow);

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
                DaysLate: daysLate));
        }

        return result;
    }

    /// <summary>
    /// Approximates "days late" from the aging buckets. Uses the oldest non-zero bucket.
    /// Returns 0 if only "Current" (due this month, not yet past-due).
    /// </summary>
    private static int ComputeDaysLate(
        LeaseBook.Modules.Accounting.Features.Ledgers.DelinquencyRow row)
    {
        if (row.Over90 > 0m) return 91;
        if (row.D61_90 > 0m) return 61;
        if (row.D31_60 > 0m) return 31;
        if (row.D1_30 > 0m) return 1;
        return 0; // Only "Current" balance — not actually delinquent.
    }
}
