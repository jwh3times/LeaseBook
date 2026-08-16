using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Runs;

namespace LeaseBook.Modules.Operations.Features.Dashboard;

/// <summary>
/// Read-only Operations policy for dashboard figures that combine Accounting balances with
/// Directory configuration. Accounting remains the source of owner equity; Operations applies the
/// same management-fee, reserve-floor, and rent-proration rules as the corresponding run previews.
/// </summary>
public sealed class DashboardMetricsService(
    IOwnerDisbursementData ownerData,
    IOwnerEquityBalances equityBalances,
    ILeaseScheduleData leaseSchedule)
{
    public async Task<OperationsDashboardMetrics> GetAsync(int year, int month, CancellationToken ct)
    {
        var owners = await ownerData.GetAsync(ct);
        var ownerIds = owners.Select(owner => owner.OwnerId).ToList();
        var equity = ownerIds.Count == 0
            ? (IReadOnlyDictionary<Guid, decimal>)new Dictionary<Guid, decimal>()
            : await equityBalances.GetAsync(ownerIds, "cash", ct);

        decimal availableToDisburse = 0m;
        var ownersAvailableToDisburse = 0;
        foreach (var owner in owners)
        {
            var ownerEquity = equity.GetValueOrDefault(owner.OwnerId, 0m);
            if (ownerEquity <= 0m)
            {
                continue;
            }

            var amount = DisbursementAmounts.Compute(
                ownerEquity,
                owner.DefaultMgmtFeeBps,
                owner.ReserveAmount).AvailableToDisburse;
            if (amount <= 0m)
            {
                continue;
            }

            availableToDisburse += amount;
            ownersAvailableToDisburse++;
        }

        var leases = await leaseSchedule.GetActiveAsync(year, month, ct);
        var scheduledRent = leases.Sum(lease =>
            Proration.Charge(lease.Rent, year, month, lease.StartDate, lease.EndDate));

        return new OperationsDashboardMetrics(
            availableToDisburse,
            ownersAvailableToDisburse,
            scheduledRent);
    }
}

public sealed record OperationsDashboardMetrics(
    decimal AvailableToDisburse,
    int OwnersAvailableToDisburse,
    decimal ScheduledRent);
