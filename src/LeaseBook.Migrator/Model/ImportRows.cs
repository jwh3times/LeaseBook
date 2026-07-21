namespace LeaseBook.Migrator.Model;

public sealed record OwnerRow(string ExternalId, string Name, decimal Reserve);
public sealed record PropertyRow(string ExternalId, string ExternalOwnerId, string Address);
public sealed record UnitRow(string ExternalId, string ExternalPropertyId, string Label, decimal Rent, string Status);
public sealed record TenantLeaseRow(string ExternalId, string ExternalUnitId, string DisplayName,
    DateOnly? StartDate, DateOnly? EndDate, decimal Rent, decimal DepositRequired, string Status);

public sealed record OwnerBalanceRow(string ExternalOwnerId, string Name, decimal CashBalance, decimal AccrualBalance);
public sealed record DepositLiabilityRow(string ExternalTenantId, string ExternalOwnerId, decimal HeldAmount);
public sealed record BankBalanceRow(string ExternalBankId, string Name, decimal BookBalance);
public sealed record TenantReceivableRow(string ExternalTenantId, string ExternalOwnerId, decimal Balance);
public sealed record HeldPmFeeRow(string ExternalBankId, string Name, decimal HeldAmount);
