using LeaseBook.Migrator.Csv;
using LeaseBook.Migrator.Model;

namespace LeaseBook.Migrator.Profiles;

/// <summary>
/// The <c>appfolio-default</c> column-mapping profiles. Header candidates are best-known guesses;
/// they are replaced with verified values as real AppFolio exports are validated. Operators can
/// also remap unrecognized columns in the wizard.
/// Plugging in real columns is editing this data, not code.
/// </summary>
public static class AppFolioProfiles
{
    public static ColumnMappingProfile For(EntityKind kind) => kind switch
    {
        EntityKind.OwnerBalances => new([
            new("external_owner_id", ["Owner ID", "Owner Id", "ID"], Required: true),
            new("name", ["Owner Name", "Name"], Required: true),
            new("cash_balance", ["Cash Balance", "Cash"], Required: true),
            new("accrual_balance", ["Accrual Balance", "Accrual"], Required: true),
        ]),
        EntityKind.DepositLiabilities => new([
            new("external_tenant_id", ["Tenant ID", "Tenant Id"], Required: true),
            new("external_owner_id", ["Owner ID", "Owner Id"], Required: true),
            new("held_amount", ["Deposit Held", "Held", "Amount"], Required: true),
        ]),
        EntityKind.BankBalances => new([
            new("external_bank_id", ["Account ID", "Bank Account", "Account"], Required: true),
            new("name", ["Account Name", "Name"], Required: true),
            new("book_balance", ["Book Balance", "Balance"], Required: true),
        ]),
        EntityKind.TenantReceivables => new([
            new("external_tenant_id", ["Tenant ID", "Tenant Id"], Required: true),
            new("external_owner_id", ["Owner ID", "Owner Id"], Required: true),
            new("balance", ["Balance Due", "Receivable", "Balance"], Required: true),
        ]),
        EntityKind.HeldPmFees => new([
            new("external_bank_id", ["Account ID", "Bank Account", "Account"], Required: true),
            new("name", ["Account Name", "Name"], Required: true),
            new("held_amount", ["Held Fees", "Unremitted Fees", "Fees Held", "Amount"], Required: true),
        ]),
        EntityKind.Owners => new([
            new("external_id", ["Owner ID", "ID"], Required: true),
            new("name", ["Owner Name", "Name"], Required: true),
            new("reserve", ["Reserve", "Reserve Amount"], Required: false),
        ]),
        EntityKind.Properties => new([
            new("external_id", ["Property ID", "ID"], Required: true),
            new("external_owner_id", ["Owner ID"], Required: true),
            new("address", ["Address", "Property Address"], Required: true),
        ]),
        EntityKind.Units => new([
            new("external_id", ["Unit ID", "ID"], Required: true),
            new("external_property_id", ["Property ID"], Required: true),
            new("label", ["Unit", "Unit Name", "Label"], Required: true),
            new("rent", ["Market Rent", "Rent"], Required: false),
            new("status", ["Status"], Required: false),
        ]),
        EntityKind.TenantsLeases => new([
            new("external_id", ["Tenant ID", "Lease ID", "ID"], Required: true),
            new("external_unit_id", ["Unit ID"], Required: true),
            new("name", ["Tenant Name", "Name"], Required: true),
            new("start", ["Lease Start", "Start"], Required: false),
            new("end", ["Lease End", "End"], Required: false),
            new("rent", ["Rent"], Required: false),
            new("deposit", ["Deposit", "Deposit Required"], Required: false),
            new("status", ["Status"], Required: false),
        ]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
