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
public sealed class UnbalancedEntryException(string message)
    : AccountingDomainException("unbalanced_entry", message);

/// <summary>A line is malformed: not exactly one of debit/credit, or amount ≤ 0, or no lines (422).</summary>
public sealed class InvalidLineException(string message)
    : AccountingDomainException("invalid_line", message);

/// <summary>No account with the requested code exists in this org (422). Resolved via RLS, not FK (M-E5).</summary>
public sealed class UnknownAccountException(string accountCode)
    : AccountingDomainException("unknown_account", $"No account with code '{accountCode}' exists in this org.")
{
    public string AccountCode { get; } = accountCode;
}

/// <summary>A pm_income line carries an owner dimension — the structural PM/owner isolation (422).</summary>
public sealed class PmIncomeOwnerDimException(string message)
    : AccountingDomainException("pm_income_owner_dim", message);

/// <summary>A posting targets a closed accounting period (409).</summary>
public sealed class PeriodClosedException(int year, int month)
    : AccountingDomainException("period_closed", $"Accounting period {year}-{month:D2} is closed.")
{
    public int Year { get; } = year;

    public int Month { get; } = month;
}

/// <summary>An application would over-draw a held liability (deposit/prepayment) — guarded events (409).</summary>
public sealed class InsufficientLiabilityException(string message)
    : AccountingDomainException("insufficient_liability", message);

/// <summary>
/// A deposit-applied-against-charges or a prepayment application would exceed the tenant's open
/// receivable (409, ADR-011 / P51). Unlike <c>PaymentReceived</c> (which auto-splits the excess to a
/// prepayment), an application has no excess path, so over-applying would silently drive the receivable
/// negative — the engine rejects instead and the composer asks the user to lower the amount. A deposit
/// applied <c>ToOwnerIncome</c> (damages) is deliberately <b>not</b> guarded.
/// </summary>
public sealed class InsufficientReceivableException(string message)
    : AccountingDomainException("insufficient_receivable", message);

/// <summary>A disbursement would take owner equity below its reserve floor (409).</summary>
public sealed class ReserveFloorException(string message)
    : AccountingDomainException("reserve_floor", message);

/// <summary>The entry is already reversed, or is itself a reversal (409).</summary>
public sealed class AlreadyReversedException(string message)
    : AccountingDomainException("already_reversed", message);

/// <summary>A bank-account line targets a finalized/locked reconciliation month (409, M4 / ADR-014).</summary>
public sealed class AccountPeriodLockedException(Guid bankAccountId, int year, int month)
    : AccountingDomainException(
        "account_period_locked",
        $"Bank account {bankAccountId} is reconciled and locked for {year}-{month:D2}; post into the open month.")
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
    : AccountingDomainException("reconciliation_not_found", $"No reconciliation with id {reconciliationId} exists.")
{
    public Guid ReconciliationId { get; } = reconciliationId;
}

/// <summary>No journal entry with the given id exists in this org (404). Resolved via RLS, so a
/// cross-org id is indistinguishable from a nonexistent one — no existence oracle.</summary>
public sealed class EntryNotFoundException(Guid entryId)
    : AccountingDomainException("entry_not_found", $"No entry with id {entryId} exists.")
{
    public Guid EntryId { get; } = entryId;
}

/// <summary>An entry with this source_ref already exists in the org (409); the existing id is carried.</summary>
public sealed class DuplicateSourceRefException(string sourceRef, Guid existingEntryId)
    : AccountingDomainException(
        "duplicate_source_ref",
        $"An entry with source_ref '{sourceRef}' already exists (id {existingEntryId}).")
{
    public string SourceRef { get; } = sourceRef;

    public Guid ExistingEntryId { get; } = existingEntryId;
}
