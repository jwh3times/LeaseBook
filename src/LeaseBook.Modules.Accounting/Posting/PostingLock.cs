using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Posting;

/// <summary>
/// Takes <c>pg_advisory_xact_lock(hashtextextended('lb:acct:' || org_id, 0))</c> (P31) on the ambient
/// connection, serializing this org's guarded postings for the rest of the transaction.
/// </summary>
internal sealed class PostingLock(DbContext db, ITenantContext tenant) : IPostingLock
{
    public async Task AcquireAsync(CancellationToken ct)
    {
        var orgId = tenant.OrgId
            ?? throw new InvalidOperationException(
                "AcquireAsync requires an ambient org context — the advisory lock key is per org.");

        // The key is the 64-bit hash of an org-namespaced string; the lock is transaction-scoped and
        // released on commit/rollback. orgId is parameterized (text) and concatenated in SQL.
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended('lb:acct:' || {orgId.ToString()}, 0))", ct);
    }
}
