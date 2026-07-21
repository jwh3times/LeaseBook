namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// Query result: imported opening-balance subledger totals + per-basis clearing residuals,
/// derived from the posted journal lines (M7 WP-4). Dispatched via
/// <see cref="LeaseBook.SharedKernel.Cqrs.ISender"/> from the host's VerificationService.
/// </summary>
public sealed record MigrationVerificationData(
    /// <summary>SUM(debit - credit) for migration_clearing lines in the cash basis. Zero = cleared.</summary>
    decimal ClearingCash,
    /// <summary>SUM(debit - credit) for migration_clearing lines in the accrual basis. Zero = cleared.</summary>
    decimal ClearingAccrual,
    /// <summary>Total owner equity (credit-normal, cash basis): SUM(credit - debit) for owner_equity lines.</summary>
    decimal OwnerEquityCashTotal,
    /// <summary>Total deposit liability (credit-normal, cash basis): SUM(credit - debit) for deposit_liability lines.</summary>
    decimal DepositLiabilityTotal,
    /// <summary>Bank book balance per bank account (debit-normal, cash+both lines).</summary>
    IReadOnlyList<BankBookBalance> BankBookBalances,
    /// <summary>
    /// Held PM fees still sitting inside the TRUST banks (credit-normal, cash+both): SUM(credit - debit)
    /// over pm_income lines whose bank_account_id belongs to a trust_bank account. Trust-bank-filtered
    /// on purpose — it mirrors invariant I2's held_pm_fees term, NOT an org-wide pm_income sum (D12).
    /// </summary>
    decimal HeldPmFeesTotal);

/// <summary>One bank account's book balance from the posted opening entries.</summary>
public sealed record BankBookBalance(Guid BankAccountId, string AccountCode, decimal Book);
