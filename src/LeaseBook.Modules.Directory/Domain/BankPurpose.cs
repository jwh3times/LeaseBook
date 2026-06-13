namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// What a bank account is used for (§C.1). The text values (<c>trust · deposit · operating</c>) mirror
/// the Accounting module's bank-purpose vocabulary, but Directory <b>must not reference the Accounting
/// assembly</b> (ADR-007 boundary) — so this is a local enum. The WP-02 provisioning seam translates
/// Directory→Accounting through a <c>Contracts</c> port, never a direct type reference.
/// </summary>
public enum BankPurpose
{
    Trust,
    Deposit,
    Operating,
}
