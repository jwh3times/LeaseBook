namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// The stable account <c>code</c>s (§C.2) every posting template resolves accounts by — never by name.
/// The five singletons are constant per org; the two bank classes are keyed by bank account id so an
/// org can hold many trust/operating banks. Shared by provisioning (WP-03), templates (WP-05), and the
/// read-model SQL (WP-06).
/// </summary>
public static class AccountCodes
{
    public const string TenantReceivable = "tenant_receivable";
    public const string OwnerEquity = "owner_equity";
    public const string SecurityDepositsHeld = "security_deposits_held";
    public const string TenantPrepayments = "tenant_prepayments";
    public const string PmIncome = "pm_income";

    /// <summary>Code for the <c>trust_bank</c> account representing a given trust/deposit bank.</summary>
    public static string TrustBank(Guid bankAccountId) => $"trust_bank:{bankAccountId}";

    /// <summary>Code for the <c>pm_operating_bank</c> account representing a given operating bank.</summary>
    public static string PmOperatingBank(Guid bankAccountId) => $"pm_operating_bank:{bankAccountId}";

    /// <summary>Singleton clearing account used as the contra leg for imported opening positions (M7/ADR-020).
    /// Nets to $0.00 in both bases once the migration ties; structurally invisible to owner statements.</summary>
    public const string MigrationClearing = "migration_clearing";
}
