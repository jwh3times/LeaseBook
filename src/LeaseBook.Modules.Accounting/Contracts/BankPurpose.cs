namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// What a bank account is used for, which fixes the chart-of-accounts class it provisions (§C.2):
/// <see cref="Trust"/> and <see cref="Deposit"/> banks hold fiduciary funds (class <c>trust_bank</c>,
/// inside the trust equation); <see cref="Operating"/> is the PM's own account (class
/// <c>pm_operating_bank</c>, outside the trust equation).
/// </summary>
public enum BankPurpose
{
    /// <summary>Operating trust account — rent in, owner disbursements out.</summary>
    Trust,

    /// <summary>Security-deposit trust account.</summary>
    Deposit,

    /// <summary>The PM's own operating account (receives swept management fees).</summary>
    Operating,
}
