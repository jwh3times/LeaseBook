using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Reporting;

/// <summary>
/// Returns the effective late-fee policy for each of the requested leases (WP-3 / NC §42-46).
/// Per-field resolution: lease override ?? org default. Used by the Operations module via the
/// <c>ILateFeePolicyData</c> port + host adapter (ADR-007 / ADR-019).
/// </summary>
public sealed record GetLateFeePolicies(IReadOnlyList<Guid> LeaseIds) : IQuery<LateFeePoliciesResponse>;

/// <summary>Response container for <see cref="GetLateFeePolicies"/>.</summary>
public sealed record LateFeePoliciesResponse(IReadOnlyList<LateFeePolicyRow> Rows);

/// <summary>
/// Resolved effective late-fee policy for one lease: each field is the per-lease override (if set)
/// or the org-wide default otherwise.
/// </summary>
public sealed record LateFeePolicyRow(
    Guid LeaseId,
    int RentDueDay,
    int GraceDays,
    LateFeeKind Kind,
    decimal FlatAmount,
    int RateBps);

internal sealed class GetLateFeePoliciesHandler(DbContext db)
    : IQueryHandler<GetLateFeePolicies, LateFeePoliciesResponse>
{
    public async Task<LateFeePoliciesResponse> Handle(GetLateFeePolicies query, CancellationToken ct)
    {
        // Fetch the org-default settings (lazily created if absent — mirroring GetOrgSettings).
        var settings = await db.Set<OrgSettings>().AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            // Lazy get-or-create with defaults. Matching GetOrgSettingsHandler's pattern.
            settings = new OrgSettings { Id = UuidV7.NewId() };
            db.Set<OrgSettings>().Add(settings);
            await db.SaveChangesAsync(ct);
            // Re-read as no-tracking after save to detach from tracker.
            settings = await db.Set<OrgSettings>().AsNoTracking().FirstAsync(ct);
        }

        if (query.LeaseIds.Count == 0)
        {
            return new LateFeePoliciesResponse([]);
        }

        // Fetch lease-level override columns for the requested leases.
        var leaseIds = query.LeaseIds;
        var leases = await db.Set<LeaseLite>()
            .AsNoTracking()
            .Where(l => leaseIds.Contains(l.Id))
            .Select(l => new
            {
                l.Id,
                l.LateFeeRentDueDayOverride,
                l.LateFeeGraceDaysOverride,
                l.LateFeeKindOverride,
                l.LateFeeAmountOverride,
                l.LateFeeRateBpsOverride,
            })
            .ToListAsync(ct);

        // Resolve per-field: lease override ?? org default.
        var rows = leases
            .Select(l => new LateFeePolicyRow(
                LeaseId: l.Id,
                RentDueDay: l.LateFeeRentDueDayOverride ?? settings.RentDueDay,
                GraceDays: l.LateFeeGraceDaysOverride ?? settings.LateFeeGraceDays,
                Kind: l.LateFeeKindOverride ?? settings.LateFeeKind,
                FlatAmount: l.LateFeeAmountOverride ?? settings.LateFeeAmount,
                RateBps: l.LateFeeRateBpsOverride ?? settings.LateFeeRateBps))
            .ToList();

        return new LateFeePoliciesResponse(rows);
    }
}
