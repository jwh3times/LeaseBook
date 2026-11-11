namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// Base of every domain rejection from the accounting engine. Carries a stable <see cref="Code"/> the
/// host's exception handler maps to an HTTP status (§C.5) — services never return raw strings as
/// errors. The full set is defined here (WP-04) so the templates (WP-05) and the host handler (WP-06)
/// share one vocabulary.
/// </summary>
public abstract class AccountingDomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Per-basis debits ≠ credits (422). I1 would be violated.</summary>
public sealed class UnbalancedEntryException(string basis, decimal debits, decimal credits)
    : AccountingDomainException(
        "unbalanced_entry",
        $"This entry does not balance: debits of {debits:0.00} do not equal credits of {credits:0.00}.")
{
    /// <summary>Diagnostic only — the basis name is an internal enum and never reaches the user.</summary>
    public string Basis { get; } = basis;

    public decimal Debits { get; } = debits;

    public decimal Credits { get; } = credits;
}

public enum InvalidLineReason
{
    NoLines,
    DebitCreditAmbiguous,
    NonPositiveAmount,
}

/// <summary>A line is malformed: not exactly one of debit/credit, or amount ≤ 0, or no lines (422).</summary>
public sealed class InvalidLineException(
    InvalidLineReason reason, string? accountCode = null, decimal? amount = null)
    : AccountingDomainException("invalid_line", Describe(reason))
{
    public InvalidLineReason Reason { get; } = reason;

    public string? AccountCode { get; } = accountCode;

    public decimal? Amount { get; } = amount;

    private static string Describe(InvalidLineReason reason) => reason switch
    {
        InvalidLineReason.NoLines => "This entry has no lines.",
        InvalidLineReason.DebitCreditAmbiguous =>
            "Each line must have either a debit or a credit, not both.",
        InvalidLineReason.NonPositiveAmount => "Each line amount must be greater than zero.",
        _ => "This entry has an invalid line.",
    };
}

/// <summary>No account with the requested code exists in this org (422). Resolved via RLS, not FK (M-E5).</summary>
public sealed class UnknownAccountException(string accountCode)
    : AccountingDomainException("unknown_account", "The requested account does not exist in this organization.")
{
    public string AccountCode { get; } = accountCode;
}

/// <summary>A pm_income line carries an owner dimension — the structural PM/owner isolation (422).</summary>
public sealed class PmIncomeOwnerDimException()
    : AccountingDomainException("pm_income_owner_dim", "PM income cannot be recorded against an owner.");

/// <summary>
/// Money moving in or out is dated after the organization's today (422, #642). "Today" is the date in
/// the organization's time zone, not the UTC date.
/// </summary>
public sealed class PostingDateInFutureException(DateOnly date, DateOnly today)
    : AccountingDomainException(
        "posting_date_in_future",
        $"The date {date:yyyy-MM-dd} is after today, {today:yyyy-MM-dd}, in the organization's time zone.")
{
    public DateOnly Date { get; } = date;

    public DateOnly Today { get; } = today;
}

/// <summary>A posting targets a closed accounting period (409).</summary>
public sealed class PeriodClosedException(int year, int month)
    : AccountingDomainException("period_closed", $"Accounting period {year}-{month:D2} is closed.")
{
    public int Year { get; } = year;

    public int Month { get; } = month;
}

public enum LiabilityKind
{
    Deposit,
    Prepayment,
    FeeSweep,
    Refund,
}

/// <summary>An application would over-draw a held liability (deposit/prepayment) — guarded events (409).</summary>
public sealed class InsufficientLiabilityException(
    LiabilityKind kind, decimal requested, decimal held, Guid? tenantId = null)
    : AccountingDomainException("insufficient_liability", Describe(kind, requested, held))
{
    public LiabilityKind Kind { get; } = kind;

    public decimal Requested { get; } = requested;

    public decimal Held { get; } = held;

    public Guid? TenantId { get; } = tenantId;

    private static string Describe(LiabilityKind kind, decimal requested, decimal held) => kind switch
    {
        LiabilityKind.Deposit =>
            $"Deposit application of {requested:0.00} exceeds the {held:0.00} held for this tenant.",
        LiabilityKind.Prepayment =>
            $"Prepayment application of {requested:0.00} exceeds the {held:0.00} held for this tenant.",
        LiabilityKind.FeeSweep =>
            $"Fee sweep of {requested:0.00} exceeds the {held:0.00} held in the operating trust account.",
        _ => $"Refund of {requested:0.00} exceeds the {held:0.00} held for this tenant.",
    };
}

public enum ReceivableSource
{
    Deposit,
    Prepayment,
    Credit,
}

/// <summary>
/// An application or a credit would exceed the tenant's open receivable (409, ADR-011 / P51, #652).
/// Unlike <c>PaymentReceived</c> (which auto-splits the excess to a prepayment), neither has an excess
/// path, so over-applying would silently drive the receivable negative — the engine rejects instead
/// and the composer asks the user to lower the amount. A credit may pass it only when explicitly allowed.
/// </summary>
public sealed class InsufficientReceivableException(
    ReceivableSource source, decimal requested, decimal owed, Guid? tenantId = null)
    : AccountingDomainException("insufficient_receivable", Describe(source, requested, owed))
{
    /// <summary>Diagnostic discriminator. Named Kind, not Source — a property named Source would
    /// hide <see cref="Exception.Source"/> (CS0114, a build error under TreatWarningsAsErrors) and
    /// give ex.Source two meanings by static type. (Execution revision: the original plan text
    /// used Source and did not compile.)</summary>
    public ReceivableSource Kind { get; } = source;

    public decimal Requested { get; } = requested;

    public decimal Owed { get; } = owed;

    public Guid? TenantId { get; } = tenantId;

    private static string Describe(ReceivableSource source, decimal requested, decimal owed) => source switch
    {
        ReceivableSource.Deposit =>
            $"Deposit application of {requested:0.00} exceeds the {owed:0.00} currently owed by this tenant.",
        ReceivableSource.Prepayment =>
            $"Prepayment application of {requested:0.00} exceeds the {owed:0.00} currently owed by this tenant.",
        _ => $"A credit of {requested:0.00} exceeds the {owed:0.00} currently owed by this tenant. " +
             "Only an administrator can allow a larger credit.",
    };
}

/// <summary>A disbursement would take owner equity below its reserve floor (409).</summary>
public sealed class ReserveFloorException(decimal amount, decimal equity, decimal reserve, Guid ownerId)
    : AccountingDomainException(
        "reserve_floor",
        $"This disbursement of {amount:0.00} would take the owner's equity from {equity:0.00} " +
        $"below their {reserve:0.00} reserve floor.")
{
    public decimal Amount { get; } = amount;

    public decimal Equity { get; } = equity;

    public decimal Reserve { get; } = reserve;

    public Guid OwnerId { get; } = ownerId;
}

public enum AlreadyReversedReason
{
    AlreadyReversed,
    IsAReversal,
}

/// <summary>The entry is already reversed, or is itself a reversal (409).</summary>
public sealed class AlreadyReversedException(Guid entryId, AlreadyReversedReason reason)
    : AccountingDomainException("already_reversed", Describe(reason))
{
    public Guid EntryId { get; } = entryId;

    public AlreadyReversedReason Reason { get; } = reason;

    private static string Describe(AlreadyReversedReason reason) => reason switch
    {
        AlreadyReversedReason.IsAReversal => "This entry is itself a reversal and cannot be voided.",
        _ => "This entry has already been voided.",
    };
}

/// <summary>A bank-account line targets a finalized/locked reconciliation month (409, M4 / ADR-014).</summary>
public sealed class AccountPeriodLockedException(Guid bankAccountId, int year, int month)
    : AccountingDomainException(
        "account_period_locked",
        $"This bank account is reconciled and locked for {year}-{month:D2}; post into the open month.")
{
    public Guid BankAccountId { get; } = bankAccountId;

    public int Year { get; } = year;

    public int Month { get; } = month;
}

/// <summary>Finalize was attempted with a non-zero reconciliation difference (409, M4).</summary>
public sealed class ReconciliationUnbalancedException(decimal difference)
    : AccountingDomainException(
        "reconciliation_unbalanced",
        $"The reconciliation difference is {difference:0.00}; clear items until it is 0.00 before finalizing.")
{
    public decimal Difference { get; } = difference;
}

public enum ReconciliationStateProblem
{
    AlreadyFinalized,
    NotFinalized,
    PeriodFinalized,
}

/// <summary>The reconciliation is not in the state the operation requires (409).</summary>
public sealed class ReconciliationStateException(
    ReconciliationStateProblem problem, Guid? reconciliationId = null, int? year = null, int? month = null)
    : AccountingDomainException("reconciliation_state", Describe(problem, year, month))
{
    public ReconciliationStateProblem Problem { get; } = problem;

    public Guid? ReconciliationId { get; } = reconciliationId;

    public int? Year { get; } = year;

    public int? Month { get; } = month;

    private static string Describe(ReconciliationStateProblem problem, int? year, int? month) => problem switch
    {
        ReconciliationStateProblem.AlreadyFinalized => "That reconciliation is already finalized.",
        ReconciliationStateProblem.NotFinalized =>
            "That reconciliation is not finalized — there is nothing to unlock.",
        _ => year is null || month is null
            ? "That reconciliation period is finalized; unlock it to re-reconcile."
            : $"The reconciliation for {year}-{month:D2} is finalized; unlock it to re-reconcile.",
    };
}

public enum ReconciliationOrderProblem
{
    /// <summary>A later month of the account is finalized: unlock it first.</summary>
    LaterMonthFinalized,

    /// <summary>An earlier month of the account is reopened: finalize it again first.</summary>
    EarlierMonthReopened,
}

public enum ReconciliationAction
{
    Start,
    Finalize,
    Unlock,
}

/// <summary>
/// An account's months finalize and unlock in order (409, #656, the ADR-014 addendum of 2026-11-09).
/// <see cref="Year"/> and <see cref="Month"/> name the month to act on first: the latest finalized
/// later month, or the earliest reopened earlier one.
/// </summary>
public sealed class ReconciliationOrderException(
    ReconciliationOrderProblem problem, ReconciliationAction action, int year, int month)
    : AccountingDomainException(CodeOf(problem), Describe(problem, action, year, month))
{
    public ReconciliationOrderProblem Problem { get; } = problem;

    public int Year { get; } = year;

    public int Month { get; } = month;

    private static string CodeOf(ReconciliationOrderProblem problem) => problem switch
    {
        ReconciliationOrderProblem.EarlierMonthReopened => "reconciliation_earlier_month_reopened",
        _ => "reconciliation_later_month_finalized",
    };

    private static string Describe(ReconciliationOrderProblem problem, ReconciliationAction action, int year, int month) =>
        problem == ReconciliationOrderProblem.EarlierMonthReopened
            ? $"{year}-{month:D2} is reopened for this account; finalize it again before a later month."
            : $"{year}-{month:D2} is finalized for this account; unlock it before " + action switch
            {
                ReconciliationAction.Unlock => "unlocking an earlier month.",
                ReconciliationAction.Start => "reconciling an earlier month.",
                _ => "finalizing an earlier month.",
            };
}

/// <summary>
/// A reconciliation for a month after the organization's current month, read in its zone (422, #656,
/// ADR-037): the month has not ended, or not begun, so no statement for it exists.
/// </summary>
public sealed class ReconciliationPeriodInFutureException(int year, int month, DateOnly today)
    : AccountingDomainException(
        "reconciliation_period_in_future",
        $"{year}-{month:D2} is after the organization's current month, {today:yyyy-MM}, so it cannot be reconciled yet.")
{
    public int Year { get; } = year;

    public int Month { get; } = month;

    public DateOnly Today { get; } = today;
}

/// <summary>
/// A month other than the organization's current one was finalized without naming its lines (422,
/// #656). Assigning a past month every cleared line dated through its end would count a check paid on
/// a later statement in the month it was written, the rule the ADR-014 addendum of 2026-11-09 rejected.
/// </summary>
public sealed class ReconciliationLinesRequiredException(int year, int month, DateOnly today)
    : AccountingDomainException(
        "reconciliation_lines_required",
        $"{year}-{month:D2} is not the organization's current month, {today:yyyy-MM}, so finalizing it must name "
        + "the lines its statement shows.")
{
    public int Year { get; } = year;

    public int Month { get; } = month;

    public DateOnly Today { get; } = today;
}

public enum ReconciliationLineProblem
{
    /// <summary>The line is dated after the month's last day (ADR-014 addendum, 2026-11-09).</summary>
    AfterMonthEnd,

    /// <summary>The id is not a bank line of the reconciliation's account.</summary>
    NotOnAccount,

    /// <summary>A reconciliation already stamped the line.</summary>
    AlreadyReconciled,
}

/// <summary>
/// A line named for a month's reconciliation cannot be assigned to it (422, #656). Only an unreconciled
/// bank line of the account, dated on or before the month's last day, can be.
/// </summary>
public sealed class ReconciliationLineNotAssignableException(
    ReconciliationLineProblem problem, int year, int month, DateOnly? lineDate = null)
    : AccountingDomainException("reconciliation_line_not_assignable", Describe(problem, year, month, lineDate))
{
    public ReconciliationLineProblem Problem { get; } = problem;

    public int Year { get; } = year;

    public int Month { get; } = month;

    public DateOnly? LineDate { get; } = lineDate;

    private static string Describe(ReconciliationLineProblem problem, int year, int month, DateOnly? lineDate) => problem switch
    {
        ReconciliationLineProblem.AfterMonthEnd when lineDate is { } date =>
            $"A line dated {date:yyyy-MM-dd} is after the end of {year}-{month:D2}, so it cannot be reconciled in that month.",
        ReconciliationLineProblem.AfterMonthEnd =>
            $"A line chosen for {year}-{month:D2} is dated after the month's end, so it cannot be reconciled in that month.",
        ReconciliationLineProblem.AlreadyReconciled =>
            $"A line chosen for {year}-{month:D2} is already reconciled in a month's reconciliation.",
        _ => $"A line chosen for {year}-{month:D2} is not a bank line of this account.",
    };
}

/// <summary>No reconciliation with the given id exists in this org (404).</summary>
public sealed class ReconciliationNotFoundException(Guid reconciliationId)
    : AccountingDomainException("reconciliation_not_found", "That reconciliation was not found.")
{
    public Guid ReconciliationId { get; } = reconciliationId;
}

/// <summary>No journal entry with the given id exists in this org (404). Resolved via RLS, so a
/// cross-org id is indistinguishable from a nonexistent one — no existence oracle.</summary>
public sealed class EntryNotFoundException(Guid entryId)
    : AccountingDomainException("entry_not_found", "That entry was not found.")
{
    public Guid EntryId { get; } = entryId;
}

/// <summary>An entry with this source_ref already exists in the org (409); the existing id is carried.</summary>
public sealed class DuplicateSourceRefException(string sourceRef, Guid existingEntryId)
    : AccountingDomainException("duplicate_source_ref", "This has already been posted.")
{
    public string SourceRef { get; } = sourceRef;

    public Guid ExistingEntryId { get; } = existingEntryId;
}

/// <summary>The rental payment already has its one permitted late-fee assessment (409).</summary>
public sealed class RentObligationAlreadyAssessedException(Guid rentObligationEntryId, Guid existingEntryId)
    : AccountingDomainException(
        "rent_obligation_already_assessed",
        "That rental payment has already been assessed a late fee.")
{
    public Guid RentObligationEntryId { get; } = rentObligationEntryId;

    public Guid ExistingEntryId { get; } = existingEntryId;
}

/// <summary>An assessment relation did not target a rent charge (422).</summary>
public sealed class InvalidAssessmentTargetException(Guid entryId)
    : AccountingDomainException(
        "invalid_assessment_target",
        "A late fee must assess a rent charge.")
{
    public Guid EntryId { get; } = entryId;
}

/// <summary>No active trust bank account exists, so trust-money operations cannot proceed (409).</summary>
public sealed class NoTrustAccountException()
    : AccountingDomainException(
        "no_trust_account",
        "No active trust bank account was found. Create one before running disbursements.");

/// <summary>
/// A refund check would draw on a tenant's held liability that sits in more than one bucket (bank, and
/// for deposits property/owner), and the caller did not name one (409, #473). The bank is never inferred
/// by guessing: the caller picks one of the held buckets.
/// </summary>
public sealed class RefundBucketAmbiguousException(Guid tenantId)
    : AccountingDomainException(
        "refund_bucket_ambiguous",
        "This tenant's held funds sit in more than one account. Choose which one to refund from.")
{
    public Guid TenantId { get; } = tenantId;
}

/// <summary>
/// Applying a prepayment would draw on a tenant's prepaid credit held in more than one bank, and the caller
/// did not name one (409, #475). As with refunds, the bank is never guessed: the caller picks one that
/// holds the prepayment.
/// </summary>
public sealed class PrepaymentBankAmbiguousException(Guid tenantId)
    : AccountingDomainException(
        "prepayment_bank_ambiguous",
        "This tenant's prepaid credit sits in more than one account. Choose which one to apply from.")
{
    public Guid TenantId { get; } = tenantId;
}

/// <summary>A refund check whose bank line is cleared or reconciled can no longer be voided (409, #473).</summary>
public sealed class RefundCheckClearedException(Guid entryId)
    : AccountingDomainException(
        "refund_check_cleared",
        "This check has cleared the bank, so it can no longer be voided.")
{
    public Guid EntryId { get; } = entryId;
}

/// <summary>What a charge against the PM's held fees was, so a refusal can name it.</summary>
public enum HeldFeesCharge
{
    ProcessorFeeShortfall,
    BankFee,
    Transfer,

    /// <summary>A staff void whose mirrored lines lower the held fees in a trust bank (#720).</summary>
    Void,

    /// <summary>
    /// A staff void whose mirrored lines lower the held fees in the PM operating bank, such as voiding a
    /// sweep it has since spent from (#720). A PM contribution cannot go there, so none is offered.
    /// </summary>
    VoidInPmOperatingBank,
}

/// <summary>
/// A charge paid from the PM fees held in one bank is larger than they are, read as the lowest held
/// balance on or after its date (409): a processor fee shortfall (ADR-053), a bank fee, or a transfer
/// out of that bank (ADR-014 addendum, #553), or a staff void that would lower them (#720). Posting it
/// would leave the bank holding less than owners and tenants are owed, so nothing is posted. The code is
/// the same whichever charge it was; Payments keys its review on the code alone. Each message names the
/// remedy: a PM contribution to that bank dated on or before the charge.
/// </summary>
public sealed class PmFeesInsufficientException(
    decimal shortfall, decimal held, Guid bankAccountId, HeldFeesCharge charge,
    string? bankName = null, DateOnly? from = null)
    : AccountingDomainException("pm_fees_insufficient", Describe(charge, shortfall, held, bankName, from))
{
    /// <summary>The amount the charge takes from the held fees (not the gap below them).</summary>
    public decimal Shortfall { get; } = shortfall;

    public decimal Held { get; } = held;

    public Guid BankAccountId { get; } = bankAccountId;

    public HeldFeesCharge Charge { get; } = charge;

    // A floor already below zero is a breach I11 reports; to the person posting, it covers nothing. A void
    // names the bank, because the entry being voided may not show which bank its lines touch.
    private static string Describe(
        HeldFeesCharge charge, decimal amount, decimal held, string? bankName, DateOnly? from) => charge switch
        {
            HeldFeesCharge.BankFee =>
                $"This account holds only {Math.Max(held, 0m):0.00} in management fees on or after the fee's date, "
                + $"less than the {amount:0.00} fee, so nothing was posted. "
                + "A PM contribution to this account dated on or before the fee covers it.",
            HeldFeesCharge.Transfer =>
                $"The account this transfer comes from holds only {Math.Max(held, 0m):0.00} in management fees on "
                + $"or after its date, less than the {amount:0.00} transfer, so nothing was posted. "
                + "A PM contribution to that account dated on or before the transfer covers it.",
            HeldFeesCharge.Void =>
                $"Voiding this entry would take the management fees held in {bankName} {amount - held:0.00} below "
                + $"zero on or after {from:yyyy-MM-dd}, so nothing was posted. Void the later charge that uses them "
                + $"first, or record a PM contribution to {bankName}.",
            HeldFeesCharge.VoidInPmOperatingBank =>
                $"Voiding this entry would take the management fees held in {bankName} {amount - held:0.00} below "
                + $"zero on or after {from:yyyy-MM-dd}, so nothing was posted. Void the later charge that uses them "
                + "first.",
            _ => "The management fees held in this account do not cover the processor fee shortfall. "
                + "A PM contribution to this account dated on or before the payout covers it.",
        };
}

/// <summary>Which end of a transfer between bank accounts a refusal is about.</summary>
public enum TransferEnd
{
    Source,
    Destination,
}

/// <summary>
/// A transfer between bank accounts names a bank that is not a trust bank at one end (422, #553). A
/// transfer moves the PM's held fees between trust accounts and nothing else: into the PM operating
/// bank it would be a fee sweep without the sweep's guard, and out of it the PM funding trust with its
/// own money, which is not a transfer (ADR-014 addendum). Nothing is posted.
/// </summary>
public sealed class TransferBankNotTrustException(Guid bankAccountId, TransferEnd end)
    : AccountingDomainException(
        "transfer_bank_not_trust",
        end == TransferEnd.Source
            ? "A transfer moves money between trust accounts, and the account it comes from is not a trust account."
            : "A transfer moves money between trust accounts, and the account it goes to is not a trust account.")
{
    public Guid BankAccountId { get; } = bankAccountId;

    public TransferEnd End { get; } = end;
}

/// <summary>
/// A PM contribution names a bank that is not a trust bank (422, #720). A contribution is the property
/// manager's own money joining its held fees in a trust account; into the PM operating bank it would be
/// the PM paying itself. Nothing is posted.
/// </summary>
public sealed class ContributionBankNotTrustException(Guid bankAccountId)
    : AccountingDomainException(
        "contribution_bank_not_trust",
        "A PM contribution is paid into a trust account, and this account is not a trust account.")
{
    public Guid BankAccountId { get; } = bankAccountId;
}

public enum PaymentReturnBlock
{
    PrepaymentConsumed,
    OwnerFundsDisbursed,
    ReturnPrecedesReceipt,
}

/// <summary>
/// A returned tenant payment cannot be posted as a linked reversal (409, #490): mirroring the receipt
/// would leave the tenant's prepaid credit or the owner's cash equity below zero on or after the return
/// date, because part of what the receipt created was applied or paid out, or the return is dated before
/// the receipt. Nothing is posted.
/// </summary>
public sealed class PaymentReturnBlockedException(Guid entryId, PaymentReturnBlock block)
    : AccountingDomainException(CodeFor(block), Describe(block))
{
    public Guid EntryId { get; } = entryId;

    public PaymentReturnBlock Block { get; } = block;

    public static string CodeFor(PaymentReturnBlock block) => block switch
    {
        PaymentReturnBlock.PrepaymentConsumed => "return_prepayment_consumed",
        PaymentReturnBlock.ReturnPrecedesReceipt => "return_precedes_receipt",
        _ => "return_owner_funds_disbursed",
    };

    private static string Describe(PaymentReturnBlock block) => block switch
    {
        PaymentReturnBlock.PrepaymentConsumed =>
            "Part of this payment became prepaid credit that has since been used, so the return cannot be posted automatically.",
        PaymentReturnBlock.ReturnPrecedesReceipt =>
            "The return is dated before the payment it returns, so it cannot be posted.",
        _ => "Funds from this payment have since left the owner's balance, so the return cannot be posted automatically.",
    };
}

/// <summary>
/// The generic entry void was asked to reverse a refund check (409, #473). Checks are voided through the
/// refund-check void, which keeps the check record, its clearance and its number in step.
/// </summary>
public sealed class RefundCheckVoidRequiredException(Guid entryId)
    : AccountingDomainException(
        "refund_check_void_required",
        "This entry is a refund check. Void it from the check instead.")
{
    public Guid EntryId { get; } = entryId;
}
