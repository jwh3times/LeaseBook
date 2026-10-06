using FluentValidation;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Banking;

/// <summary>
/// Marks bank journal lines <c>cleared</c> (P62/P68): the write half of clearing, called by the register
/// UI and the Banking import/match adapter (ADR-007). Idempotent — already-cleared lines are a no-op, and
/// <c>reconciled</c> lines are never downgraded. Status lives in <c>bank_line_status</c>, not the journal,
/// so this touches no posted row.
/// <para>
/// The bank lines of one processor payout are one statement line at the bank, so they clear together or
/// not at all (ADR-053): naming any of them clears, or unclears, every one of them. This is the one
/// clearance writer short of reconcile finalize, so no caller can clear part of a payout.
/// </para>
/// </summary>
public sealed record ApplyClearances(IReadOnlyCollection<Guid> JournalLineIds, bool Cleared = true)
    : ICommand<ClearancesResult>;

public sealed record ClearancesResult(int Affected);

internal sealed class ApplyClearancesHandler(DbContext db, IOrgContext tenant)
    : ICommandHandler<ApplyClearances, ClearancesResult>
{
    public async Task<ClearancesResult> Handle(ApplyClearances command, CancellationToken ct)
    {
        var ids = command.JournalLineIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new ClearancesResult(0);
        }

        var orgId = tenant.OrgId
            ?? throw new InvalidOperationException("ApplyClearances requires an ambient organization context.");

        // Only bank-account lines may be cleared. RLS scopes the count to this org, so a foreign or
        // non-bank id makes the count fall short and the whole batch is rejected (no silent partial clear).
        var bankLineCount = await db.Set<JournalLine>()
            .CountAsync(l => ids.Contains(l.Id)
                && (l.AccountClass == AccountClass.TrustBank || l.AccountClass == AccountClass.PmOperatingBank), ct);
        if (bankLineCount != ids.Length)
        {
            throw new ValidationException("Every id to clear must be a bank-account journal line in this org.");
        }

        ids = await WithWholePayoutsAsync(ids, ct);

        int affected;
        if (command.Cleared)
        {
            // Clear (tick): insert a state row where none exists, or flip an 'uncleared' one. A
            // 'reconciled' row is left untouched (the ON CONFLICT WHERE guard) — clearing never un-reconciles.
            affected = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO bank_line_status (journal_line_id, org_id, status, cleared_at, created_at, updated_at)
                SELECT id, {orgId}, 'cleared', now(), now(), now()
                FROM journal_lines
                WHERE id = ANY({ids})
                ON CONFLICT (journal_line_id) DO UPDATE
                  SET status = 'cleared', cleared_at = now(), updated_at = now()
                  WHERE bank_line_status.status = 'uncleared'
                """, ct);
        }
        else
        {
            // Unclear (untick): flip 'cleared' back to 'uncleared'. Absent rows are already uncleared;
            // 'reconciled' rows are never downgraded here (unlock is the only path out of reconciled).
            affected = await db.Database.ExecuteSqlAsync(
                $"""
                UPDATE bank_line_status
                SET status = 'uncleared', cleared_at = NULL, updated_at = now()
                WHERE journal_line_id = ANY({ids}) AND status = 'cleared'
                """, ct);
        }

        return new ClearancesResult(affected);
    }

    private static readonly AccountClass[] BankClasses = [AccountClass.TrustBank, AccountClass.PmOperatingBank];

    // Widens the ids to every bank line of each payout they touch. Payouts are few per request, so one
    // prefix read per payout is cheaper than a pattern join across the journal.
    private async Task<Guid[]> WithWholePayoutsAsync(Guid[] ids, CancellationToken ct)
    {
        var touched = await (
            from line in db.Set<JournalLine>()
            join entry in db.Set<JournalEntry>() on line.EntryId equals entry.Id
            where ids.Contains(line.Id) && entry.SourceRef != null && entry.SourceRef.StartsWith(PayoutSourceRef.Prefix)
            select new { line.BankAccountId, entry.SourceRef }).ToListAsync(ct);

        var payouts = touched
            .Select(t => (t.BankAccountId, Reference: PayoutSourceRef.ReferenceOf(t.SourceRef)))
            .Where(t => t.Reference is not null)
            .Distinct()
            .ToList();
        if (payouts.Count == 0)
        {
            return ids;
        }

        var all = ids.ToHashSet();
        foreach (var (bankAccountId, reference) in payouts)
        {
            var prefix = PayoutSourceRef.GroupPrefix(reference!);
            all.UnionWith(await (
                from line in db.Set<JournalLine>()
                join entry in db.Set<JournalEntry>() on line.EntryId equals entry.Id
                where entry.SourceRef != null && entry.SourceRef.StartsWith(prefix)
                    && line.BankAccountId == bankAccountId && BankClasses.Contains(line.AccountClass)
                select line.Id).ToListAsync(ct));
        }

        return [.. all];
    }
}
