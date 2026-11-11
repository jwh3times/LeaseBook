using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Persistence;
using LeaseBook.Modules.Accounting.Posting;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Accounting.Features.Reconciliation;

/// <summary>The state of one reconciliation, with the live difference (statement − cleared) the UI drives to $0.</summary>
public sealed record ReconciliationView(
    Guid Id, string Status, Guid BankAccountId, int Year, int Month,
    decimal StatementEndingBalance, decimal ClearedBalance, decimal Difference, DateTime? FinalizedAt);

/// <summary>
/// One bank line of the account as reconciliation reads it: its date, description, signed amount (a
/// deposit positive, a withdrawal negative) and clearance status (<c>cleared</c> or <c>uncleared</c>).
/// <paramref name="PayoutReference"/> names the processor payout it belongs to, or is null: a payout's
/// lines are one statement line at the bank, so finalize assigns them together (ADR-053), and a screen
/// ticks them together.
/// </summary>
public sealed record ReconciliationLine(
    Guid JournalLineId, DateOnly EntryDate, string? Description, decimal Amount, string Status, string? PayoutReference);

/// <summary>
/// An account's months finalize and unlock in order, and none is reconciled before it ends (#656, the
/// ADR-014 addendum of 2026-11-09). A month never reconciled blocks nothing.
/// </summary>
internal static class ReconciliationOrder
{
    /// <summary>Refuses a month after the organization's current month, in its zone (ADR-037).</summary>
    public static async Task EnsureNotFutureAsync(PostingCalendar calendar, int year, int month, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        if ((year * 12) + month > (today.Year * 12) + today.Month)
        {
            throw new ReconciliationPeriodInFutureException(year, month, today);
        }
    }

    /// <summary>Refuses while a later month of the account is finalized, naming the latest one.</summary>
    public static async Task EnsureNoLaterFinalizedAsync(
        DbContext db, Guid bankAccountId, int year, int month, ReconciliationAction action, CancellationToken ct)
    {
        var period = (year * 12) + month;
        var latest = await db.Set<BankReconciliation>().AsNoTracking()
            .Where(r => r.BankAccountId == bankAccountId && r.Status == ReconciliationStatus.Finalized
                && (r.PeriodYear * 12) + r.PeriodMonth > period)
            .OrderByDescending(r => r.PeriodYear).ThenByDescending(r => r.PeriodMonth)
            .Select(r => new { r.PeriodYear, r.PeriodMonth })
            .FirstOrDefaultAsync(ct);
        if (latest is not null)
        {
            throw new ReconciliationOrderException(
                ReconciliationOrderProblem.LaterMonthFinalized, action, latest.PeriodYear, latest.PeriodMonth);
        }
    }

    /// <summary>Refuses while an earlier month of the account is reopened, naming the earliest one.</summary>
    public static async Task EnsureNoEarlierReopenedAsync(
        DbContext db, Guid bankAccountId, int year, int month, CancellationToken ct)
    {
        var period = (year * 12) + month;
        var earliest = await db.Set<BankReconciliation>().AsNoTracking()
            .Where(r => r.BankAccountId == bankAccountId && r.Status == ReconciliationStatus.Reopened
                && (r.PeriodYear * 12) + r.PeriodMonth < period)
            .OrderBy(r => r.PeriodYear).ThenBy(r => r.PeriodMonth)
            .Select(r => new { r.PeriodYear, r.PeriodMonth })
            .FirstOrDefaultAsync(ct);
        if (earliest is not null)
        {
            throw new ReconciliationOrderException(
                ReconciliationOrderProblem.EarlierMonthReopened, ReconciliationAction.Finalize,
                earliest.PeriodYear, earliest.PeriodMonth);
        }
    }
}

/// <summary>
/// A month's cleared balance (#656, the ADR-014 addendum of 2026-11-09), shared by the live difference,
/// finalize and invariant I12 so that the three can never disagree. It is the bank lines stamped by the
/// account's reconciliations for the month or an earlier one, plus the cleared, unreconciled lines
/// assigned to the month while it is finalized. A line's date does not decide its month; only a line
/// dated on or before the month's last day can be assigned to it. Own-module SQL (ADR-007).
/// </summary>
internal static class ReconciliationSql
{
    public static DateOnly MonthEnd(int year, int month) => new(year, month, DateTime.DaysInMonth(year, month));

    /// <summary>
    /// The net of the account's bank lines that its reconciliations for <paramref name="year"/>-<paramref name="month"/>
    /// or an earlier month stamped. For a finalized month this is its whole cleared balance.
    /// </summary>
    public static async Task<decimal> ReconciledThroughAsync(
        DbContext db, Guid bankAccountId, int year, int month, CancellationToken ct)
    {
        var period = (year * 12) + month;
        return (await db.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0)), 0) AS "Value"
            FROM journal_lines jl
            JOIN bank_line_status s ON s.journal_line_id = jl.id
            JOIN bank_reconciliations r ON r.id = s.reconciliation_id
            WHERE s.status = 'reconciled'
              AND r.bank_account_id = {bankAccountId}
              AND (r.period_year * 12) + r.period_month <= {period}
              AND jl.bank_account_id = {bankAccountId}
              AND jl.account_class IN ('trust_bank', 'pm_operating_bank')
              AND jl.basis IN ('cash', 'both')
            """).ToListAsync(ct)).Single();
    }

    /// <summary>
    /// The account's bank lines no reconciliation has stamped, dated on or before <paramref name="through"/>:
    /// what a month may be assigned, cleared or not.
    /// </summary>
    public static Task<List<ReconciliationLine>> UnreconciledThroughAsync(
        DbContext db, Guid bankAccountId, DateOnly through, CancellationToken ct) =>
        UnreconciledAsync(db, bankAccountId, through, null, ct);

    /// <summary>The named lines among the account's unreconciled bank lines, whatever their date.</summary>
    public static Task<List<ReconciliationLine>> UnreconciledByIdAsync(
        DbContext db, Guid bankAccountId, Guid[] ids, CancellationToken ct) =>
        UnreconciledAsync(db, bankAccountId, null, ids, ct);

    private static async Task<List<ReconciliationLine>> UnreconciledAsync(
        DbContext db, Guid bankAccountId, DateOnly? through, Guid[]? ids, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<UnreconciledRow>(
            $"""
            SELECT jl.id AS journal_line_id, e.entry_date, e.description, e.source_ref,
                   COALESCE(jl.debit, 0) - COALESCE(jl.credit, 0) AS amount,
                   COALESCE(s.status, 'uncleared') AS status
            FROM journal_lines jl
            JOIN journal_entries e ON e.id = jl.entry_id
            LEFT JOIN bank_line_status s ON s.journal_line_id = jl.id
            WHERE jl.bank_account_id = {bankAccountId}
              AND jl.account_class IN ('trust_bank', 'pm_operating_bank')
              AND jl.basis IN ('cash', 'both')
              AND COALESCE(s.status, 'uncleared') <> 'reconciled'
              AND ({through}::date IS NULL OR e.entry_date <= {through})
              AND ({ids}::uuid[] IS NULL OR jl.id = ANY({ids}))
            ORDER BY e.entry_date, jl.id
            """).ToListAsync(ct);

        return rows
            .Select(r => new ReconciliationLine(
                r.JournalLineId, r.EntryDate, r.Description, r.Amount, r.Status, PayoutSourceRef.ReferenceOf(r.SourceRef)))
            .ToList();
    }

    private sealed record UnreconciledRow(
        Guid JournalLineId, DateOnly EntryDate, string? Description, string? SourceRef, decimal Amount, string Status);

    /// <summary>Whether a month is the organization's current one.</summary>
    public static bool IsCurrentMonth(BankReconciliation recon, DateOnly today) =>
        recon.PeriodYear == today.Year && recon.PeriodMonth == today.Month;

    /// <summary>
    /// What finalizing the organization's current month assigns when no lines are named: every cleared,
    /// unreconciled line dated through its last day, which is every cleared line, since no bank line can
    /// be dated after the organization's today. Any other month names its lines (#656).
    /// </summary>
    public static async Task<List<ReconciliationLine>> DefaultAssignmentAsync(
        DbContext db, BankReconciliation recon, CancellationToken ct) =>
        (await UnreconciledThroughAsync(db, recon.BankAccountId, MonthEnd(recon.PeriodYear, recon.PeriodMonth), ct))
            .Where(l => l.Status == "cleared")
            .ToList();

    /// <summary>
    /// Projects a reconciliation and its live cleared balance + difference. A finalized month's cleared
    /// balance is what it and the months before it stamped. The current month, open, adds the lines
    /// finalizing it would assign by default. Any other open month adds nothing: its lines are named, and
    /// until they are none is ticked.
    /// </summary>
    public static async Task<ReconciliationView> BuildAsync(
        DbContext db, BankReconciliation recon, DateOnly today, CancellationToken ct)
    {
        var cleared = await ReconciledThroughAsync(db, recon.BankAccountId, recon.PeriodYear, recon.PeriodMonth, ct);
        if (recon.Status != ReconciliationStatus.Finalized && IsCurrentMonth(recon, today))
        {
            cleared += (await DefaultAssignmentAsync(db, recon, ct)).Sum(l => l.Amount);
        }

        var statement = recon.StatementEndingBalance.Amount;
        return new ReconciliationView(
            recon.Id, ReconciliationStatusConverter.ToDb(recon.Status), recon.BankAccountId,
            recon.PeriodYear, recon.PeriodMonth, statement, cleared, statement - cleared, recon.FinalizedAt);
    }
}
