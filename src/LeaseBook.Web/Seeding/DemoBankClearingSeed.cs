using LeaseBook.Modules.Accounting.Domain;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Seeding;

/// <summary>
/// Seeds the demo org's clearance state (P72) so the bank register reproduces the prototype's mix: the
/// <b>Operating Trust</b> shows three uncleared items (the −8,200 owner disbursement plus a 1,380 and a
/// 1,450 deposit — net −5,370), so its cleared balance (254,300.14) exceeds book (248,930.14); the
/// <b>Security Deposit Trust</b> is fully cleared (uncleared 0). A reconcile-to-$0 demo (and the M4 e2e)
/// then ticks those three to zero and finalizes.
/// <para>
/// Clearance is mutable operational metadata on the <c>bank_line_status</c> side table (P62) — never a
/// journal row, so the journal/golden figures stay byte-identical (book is unchanged; only the
/// cleared/uncleared split moves). Runs after <see cref="DemoJournalSeed"/> in the seeder's ambient org
/// transaction, reading the just-posted bank lines through the shared context (the same way the other demo
/// seed steps read their module's rows). Idempotent — skips once any state row exists, so a re-seed against
/// an already-seeded db (where the journal exists but no clearances do) still backfills it.
/// </para>
/// </summary>
internal static class DemoBankClearingSeed
{
    public static async Task SeedAsync(DbContext db, CancellationToken ct)
    {
        if (await db.Set<BankLineState>().AnyAsync(ct))
        {
            return; // already seeded (idempotent)
        }

        // The bank-account asset lines for the two trust banks (the register's own filter, P69): lines
        // posted TO the bank account, cash/both. RLS scopes the read to the demo org via the ambient context.
        var lines = await db.Database.SqlQuery<BankAssetLine>(
            $"""
            SELECT jl.id AS journal_line_id,
                   e.entry_date AS entry_date,
                   jl.debit,
                   jl.credit,
                   jl.bank_account_id
            FROM journal_lines jl
            JOIN journal_entries e ON e.id = jl.entry_id
            WHERE jl.account_class IN ('trust_bank', 'pm_operating_bank')
              AND jl.basis IN ('cash', 'both')
              AND jl.bank_account_id IN ({DemoIds.OperBank}, {DemoIds.DepositBank})
            """).ToListAsync(ct);

        var uncleared = ChooseOperatingUnclearedLines(lines);

        // Everything but the three uncleared lines is cleared. Written as a raw upsert — bank_line_status is
        // operational metadata keyed by journal_line_id (no surrogate Id), so it is written through SQL like
        // the ApplyClearances/finalize commands, not EF change-tracking (which would run the audit pass).
        var clearedIds = lines
            .Where(l => !uncleared.Contains(l.JournalLineId))
            .Select(l => l.JournalLineId)
            .ToArray();

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO bank_line_status (journal_line_id, org_id, status, cleared_at, created_at, updated_at)
            SELECT id, {DemoSeeder.DemoOrgId}, 'cleared', now(), now(), now()
            FROM journal_lines
            WHERE id = ANY({clearedIds})
            """, ct);
    }

    /// <summary>
    /// The three Operating Trust lines left uncleared (net −5,370 → cleared 254,300.14 &gt; book 248,930.14):
    /// the unique −8,200 owner disbursement, the unique +1,380 Pryor payment, and the most-recent +1,450
    /// deposit (Jasmine Carter's May payment). All other bank-asset lines are cleared.
    /// </summary>
    private static HashSet<Guid> ChooseOperatingUnclearedLines(List<BankAssetLine> lines)
    {
        var oper = lines.Where(l => l.BankAccountId == DemoIds.OperBank).ToList();
        var disbursement = oper.Single(l => l.Credit == 8_200m);
        var pryorPayment = oper.Single(l => l.Debit == 1_380m);
        var carterPayment = oper.Where(l => l.Debit == 1_450m).OrderByDescending(l => l.EntryDate).First();

        return [disbursement.JournalLineId, pryorPayment.JournalLineId, carterPayment.JournalLineId];
    }

    private sealed record BankAssetLine(
        Guid JournalLineId, DateOnly EntryDate, decimal? Debit, decimal? Credit, Guid BankAccountId);
}
