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
}

/// <summary>
/// An application would exceed the tenant's open receivable (409, ADR-011 / P51). Unlike
/// <c>PaymentReceived</c> (which auto-splits the excess to a prepayment), an application has no
/// excess path, so over-applying would silently drive the receivable negative — the engine rejects
/// instead and the composer asks the user to lower the amount.
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

    private static string Describe(ReceivableSource source, decimal requested, decimal owed) =>
        source == ReceivableSource.Deposit
            ? $"Deposit application of {requested:0.00} exceeds the {owed:0.00} currently owed by this tenant."
            : $"Prepayment application of {requested:0.00} exceeds the {owed:0.00} currently owed by this tenant.";
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
public sealed class ReconciliationUnbalancedException(string message)
    : AccountingDomainException("reconciliation_unbalanced", message);

/// <summary>The reconciliation is not in the state the operation requires — e.g. finalizing a finalized one (409).</summary>
public sealed class ReconciliationStateException(string message)
    : AccountingDomainException("reconciliation_state", message);

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
