using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Refunds;

/// <summary>
/// One held-liability bucket a refund can draw on (#473). A deposit bucket is the exact
/// (bank, property, owner) attribution its collection carried (ADR-026); a prepayment bucket is per bank
/// and owner-null. Naming a bucket is how a caller resolves an ambiguous refund — never by naming a bank
/// the liability is not held in.
/// </summary>
public sealed record RefundBucket(Guid BankAccountId, Guid? PropertyId, Guid? OwnerId);

/// <summary>A tenant's positive held balance in one bucket.</summary>
public sealed record RefundableBalance(
    Guid TenantId, string Source, Guid BankAccountId, Guid? PropertyId, Guid? OwnerId, decimal Held);

/// <summary>
/// The Accounting side of a refund check: its <c>source_ref</c> convention, posted description, and the
/// held-bucket read both the post and the query share. Accounting owns every word of journal text, so
/// the check number reaches the description here rather than from the caller.
/// </summary>
internal static class RefundChecks
{
    /// <summary>Marks a <c>RefundIssued</c> entry as a refund check; the generic void refuses these.</summary>
    public const string SourceRefPrefix = "refund-check:";

    public static readonly IReadOnlyDictionary<string, RefundSource> Sources =
        new Dictionary<string, RefundSource>(StringComparer.OrdinalIgnoreCase)
        {
            ["deposit"] = RefundSource.Deposits,
            ["prepayment"] = RefundSource.Prepayments,
        };

    public static string SourceRef(Guid checkId) => SourceRefPrefix + checkId;

    /// <summary>Deterministic, so a second void of the same check is a duplicate rather than a re-post.</summary>
    public static string VoidSourceRef(Guid entryId) => $"refund-check-void:{entryId}";

    public static string Description(int checkNumber, RefundSource source) =>
        $"Refund check #{checkNumber} — " + (source == RefundSource.Deposits ? "security deposit" : "prepaid credit");

    public static bool IsRefundCheck(JournalEntry entry) =>
        entry.EventType == "RefundIssued"
        && entry.SourceRef is not null
        && entry.SourceRef.StartsWith(SourceRefPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Positive held balances per bucket. Prepayment lines are grouped per bank only, projecting null
    /// property/owner, so the bucket matches the per-bank guard in the <c>RefundIssued</c> template. A
    /// prepayment bucket is also capped at the tenant's prepayment total, as that guard is: applying a
    /// prepayment against another bank can leave one bucket positive and another negative.
    /// </summary>
    public static async Task<IReadOnlyList<RefundableBalance>> ReadAsync(
        DbContext db, IReadOnlyCollection<Guid> tenantIds, CancellationToken ct)
    {
        var ids = tenantIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await db.Database.SqlQuery<BucketRow>(
            $"""
            SELECT jl.tenant_id,
                   CASE WHEN a.code = {AccountCodes.SecurityDepositsHeld} THEN 'deposit' ELSE 'prepayment' END AS source,
                   jl.bank_account_id,
                   CASE WHEN a.code = {AccountCodes.SecurityDepositsHeld} THEN jl.property_id END AS property_id,
                   CASE WHEN a.code = {AccountCodes.SecurityDepositsHeld} THEN jl.owner_id END AS owner_id,
                   SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)) AS held
            FROM journal_lines jl
            JOIN accounts a ON a.id = jl.account_id
            WHERE a.code IN ({AccountCodes.SecurityDepositsHeld}, {AccountCodes.TenantPrepayments})
              AND jl.tenant_id = ANY({ids})
              AND jl.bank_account_id IS NOT NULL
              AND jl.basis IN ('cash', 'both')
            GROUP BY 1, 2, 3, 4, 5
            ORDER BY 1, 2, 3
            """).ToListAsync(ct);

        var prepaymentTotals = rows.Where(r => r.Source == "prepayment")
            .GroupBy(r => r.TenantId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Held));
        return rows
            .Select(r => r with
            {
                Held = r.Source == "prepayment" ? Math.Min(r.Held, prepaymentTotals[r.TenantId]) : r.Held,
            })
            .Where(r => r.Held > 0)
            .Select(r => new RefundableBalance(r.TenantId, r.Source, r.BankAccountId, r.PropertyId, r.OwnerId, r.Held))
            .ToArray();
    }

    private sealed record BucketRow(
        Guid TenantId, string Source, Guid BankAccountId, Guid? PropertyId, Guid? OwnerId, decimal Held);
}
