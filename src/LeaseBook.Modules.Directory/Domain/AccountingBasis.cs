namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// The org's default accounting basis (§C.1, P46). <see cref="Cash"/> is first so it is the CLR default,
/// matching the <c>org_settings.accounting_basis DEFAULT 'cash'</c> store default. The M1 read endpoints
/// default their <c>basis</c> parameter to this org preference.
/// </summary>
public enum AccountingBasis
{
    Cash,
    Accrual,
}
