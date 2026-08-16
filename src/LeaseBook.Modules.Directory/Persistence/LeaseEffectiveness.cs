using LeaseBook.Modules.Directory.Domain;

namespace LeaseBook.Modules.Directory.Persistence;

/// <summary>
/// Canonical query predicates for deciding which non-pending lease terms apply to a date or period.
/// Lifecycle status alone never establishes financial or current-context attribution.
/// </summary>
public static class LeaseEffectiveness
{
    /// <summary>Filters to non-pending leases whose inclusive term contains <paramref name="date"/>.</summary>
    public static IQueryable<LeaseLite> EffectiveOn(this IQueryable<LeaseLite> leases, DateOnly date) =>
        leases.Where(lease =>
            lease.Status != LeaseStatus.Pending
            && (lease.StartDate == null || lease.StartDate <= date)
            && (lease.EndDate == null || lease.EndDate >= date));

    /// <summary>Filters to non-pending leases whose inclusive term overlaps the inclusive period.</summary>
    public static IQueryable<LeaseLite> EffectiveDuring(
        this IQueryable<LeaseLite> leases,
        DateOnly periodStart,
        DateOnly periodEnd) =>
        leases.Where(lease =>
            lease.Status != LeaseStatus.Pending
            && (lease.StartDate == null || lease.StartDate <= periodEnd)
            && (lease.EndDate == null || lease.EndDate >= periodStart));
}
