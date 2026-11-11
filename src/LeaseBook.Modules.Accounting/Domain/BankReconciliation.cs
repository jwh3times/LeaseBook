using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Accounting.Domain;

/// <summary>
/// A bank reconciliation for one (org, bank account, year, month) (M4 / §B.2 / ADR-014). Created
/// <see cref="ReconciliationStatus.InProgress"/> with the statement ending balance; finalized only at a
/// zero difference, which marks the account's cleared lines <see cref="BankLineStatus.Reconciled"/>,
/// stores an immutable <see cref="ReportSnapshot"/>, and locks the (account, month) against further bank
/// postings. Unlock (PMAdmin + reason) flips it to <see cref="ReconciliationStatus.Reopened"/>.
/// </summary>
public sealed class BankReconciliation : IOrgScoped
{
    private BankReconciliation()
    {
        // EF + the factory below.
    }

    private BankReconciliation(Guid bankAccountId, int year, int month, Money statementEndingBalance)
    {
        Id = UuidV7.NewId();
        BankAccountId = bankAccountId;
        PeriodYear = year;
        PeriodMonth = month;
        StatementEndingBalance = statementEndingBalance;
        Status = ReconciliationStatus.InProgress;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; set; }

    public Guid BankAccountId { get; private set; }

    public int PeriodYear { get; private set; }

    public int PeriodMonth { get; private set; }

    public Money StatementEndingBalance { get; private set; }

    public ReconciliationStatus Status { get; private set; }

    public DateTime? FinalizedAt { get; private set; }

    public Guid? FinalizedBy { get; private set; }

    /// <summary>The report stored verbatim at finalize — returned as-is, never recomputed.</summary>
    public string? ReportSnapshot { get; private set; }

    /// <summary>Why a PMAdmin reopened this reconciliation (audit), if it was reopened.</summary>
    public string? ReopenReason { get; private set; }

    /// <summary>
    /// When the month was first finalized. <see cref="FinalizedAt"/> moves on every finalize; this does
    /// not, so a month finalized again still says when it first closed (#656).
    /// </summary>
    public DateTime? FirstFinalizedAt { get; private set; }

    /// <summary>Who last reopened it: the administrator whose decision, with its reason, is the unlock (#656).</summary>
    public Guid? ReopenedBy { get; private set; }

    public DateTime CreatedAt { get; private set; }

    internal static BankReconciliation Start(Guid bankAccountId, int year, int month, Money statementEndingBalance) =>
        new(bankAccountId, year, month, statementEndingBalance);

    /// <summary>Re-enter the statement ending balance on a not-yet-finalized reconciliation.</summary>
    internal void SetStatementBalance(Money statementEndingBalance)
    {
        EnsureNotFinalized();
        StatementEndingBalance = statementEndingBalance;
    }

    /// <summary>Finalize at a zero difference — allowed from in-progress or (after unlock) reopened.</summary>
    internal void Finalize(Guid? actor, string reportSnapshot, DateTime at)
    {
        EnsureNotFinalized();
        // Before FinalizedAt moves: a month reopened before the first-finalized time was kept has only
        // its last finalize time, and that is the first one this row can still name.
        FirstFinalizedAt ??= FinalizedAt ?? at;
        Status = ReconciliationStatus.Finalized;
        FinalizedBy = actor;
        ReportSnapshot = reportSnapshot;
        FinalizedAt = at;
    }

    internal void Reopen(string reason, Guid? by)
    {
        if (Status != ReconciliationStatus.Finalized)
        {
            throw new InvalidOperationException("Only a finalized reconciliation can be reopened.");
        }

        Status = ReconciliationStatus.Reopened;
        ReopenReason = reason;
        ReopenedBy = by;
        // A row finalized before the first-finalized time was kept still knows it: it is the time
        // being replaced only now.
        FirstFinalizedAt ??= FinalizedAt;
    }

    private void EnsureNotFinalized()
    {
        if (Status == ReconciliationStatus.Finalized)
        {
            throw new InvalidOperationException($"Reconciliation {Id} is already finalized; unlock it first.");
        }
    }
}
