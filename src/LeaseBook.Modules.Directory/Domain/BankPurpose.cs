namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// The immutable classification of a bank account (§C.1). <see cref="Trust"/> is the operating trust
/// account and <see cref="Deposit"/> is the security-deposit trust account; both are inside the trust
/// equation. <see cref="Operating"/> is the PM operating account, outside the trust equation. The text
/// values (<c>trust · deposit · operating</c>) mirror Accounting, but Directory <b>must not reference
/// the Accounting assembly</b> (ADR-007 boundary), so this is a local enum. The WP-02 provisioning
/// seam translates Directory→Accounting through a <c>Contracts</c> port, never a direct type reference.
/// </summary>
public enum BankPurpose
{
    /// <summary>Operating trust account — inside the trust equation.</summary>
    Trust,

    /// <summary>Security-deposit trust account — inside the trust equation.</summary>
    Deposit,

    /// <summary>PM operating account — outside the trust equation.</summary>
    Operating,
}
