using FluentValidation;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Banking;

/// <summary>
/// Marks bank journal lines <c>cleared</c> (P62/P68): the write half of clearing, called by the register
/// UI and the Banking import/match adapter (ADR-007). Idempotent — already-cleared lines are a no-op, and
/// <c>reconciled</c> lines are never downgraded. Status lives in <c>bank_line_status</c>, not the journal,
/// so this touches no posted row.
/// </summary>
public sealed record ApplyClearances(IReadOnlyCollection<Guid> JournalLineIds, bool Cleared = true)
    : ICommand<ClearancesResult>;

public sealed record ClearancesResult(int Affected);

internal sealed class ApplyClearancesHandler(DbContext db, ITenantContext tenant)
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
            ?? throw new InvalidOperationException("ApplyClearances requires an ambient org context.");

        // Only bank-account lines may be cleared. RLS scopes the count to this org, so a foreign or
        // non-bank id makes the count fall short and the whole batch is rejected (no silent partial clear).
        var bankLineCount = await db.Set<JournalLine>()
            .CountAsync(l => ids.Contains(l.Id)
                && (l.AccountClass == AccountClass.TrustBank || l.AccountClass == AccountClass.PmOperatingBank), ct);
        if (bankLineCount != ids.Length)
        {
            throw new ValidationException("Every id to clear must be a bank-account journal line in this org.");
        }

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
}
