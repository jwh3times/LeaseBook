using LeaseBook.Modules.Accounting.Features.Migration;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Onboarding;

/// <summary>
/// Owns the organization-wide migration cutover-date contract. The immutable ADR-020 opening
/// journal entries are the source of truth; the transaction-scoped advisory lock serializes the
/// race in which two first imports could otherwise establish different dates concurrently.
/// </summary>
public sealed class MigrationCutoverDate(
    DbContext db,
    ISender sender,
    IOrgContext orgContext)
{
    public async Task<DateOnly?> GetAsync(CancellationToken ct)
    {
        var result = await sender.Query(new GetOpeningCutoverDate(), ct);
        return result.CutoverDate;
    }

    public async Task EnsureMatchesAsync(DateOnly requestedDate, CancellationToken ct)
    {
        var orgId = orgContext.OrgId
            ?? throw new InvalidOperationException(
                "Organization context is required to establish a migration cutover date.");

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "A transaction is required to establish a migration cutover date.");
        }

        var lockKey = $"leasebook:migration-cutover:{orgId:D}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);

        var establishedDate = await GetAsync(ct);
        if (establishedDate is not null && establishedDate != requestedDate)
        {
            throw new OnboardingConflictException(
                "cutover_date_mismatch",
                $"The requested cutover date ({requestedDate:yyyy-MM-dd}) does not match the imported cutover date ({establishedDate:yyyy-MM-dd}). Changing the cutover date requires re-provisioning.");
        }
    }
}
