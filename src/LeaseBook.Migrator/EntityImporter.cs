using System.Globalization;
using LeaseBook.Migrator.Csv;
using LeaseBook.Migrator.Model;

namespace LeaseBook.Migrator;

/// <summary>Typed binders over <see cref="CsvImporter"/> for each entity kind. Pure (spec §6).</summary>
public static class EntityImporter
{
    public static ImportResult<OwnerBalanceRow> ReadOwnerBalances(Stream csv, ColumnMappingProfile profile) =>
        CsvImporter.Read(csv, profile, ctx =>
        {
            if (!Dec(ctx.Cells, "cash_balance", out var cash))
                return ctx.Reject<OwnerBalanceRow>("cash_balance", "not a number");
            if (!Dec(ctx.Cells, "accrual_balance", out var accr))
                return ctx.Reject<OwnerBalanceRow>("accrual_balance", "not a number");
            return new OwnerBalanceRow(ctx.Cells["external_owner_id"], ctx.Cells["name"], cash, accr);
        });

    public static ImportResult<DepositLiabilityRow> ReadDepositLiabilities(Stream csv, ColumnMappingProfile profile) =>
        CsvImporter.Read(csv, profile, ctx =>
            Dec(ctx.Cells, "held_amount", out var held)
                ? new DepositLiabilityRow(ctx.Cells["external_tenant_id"], ctx.Cells["external_owner_id"], held)
                : ctx.Reject<DepositLiabilityRow>("held_amount", "not a number"));

    public static ImportResult<BankBalanceRow> ReadBankBalances(Stream csv, ColumnMappingProfile profile) =>
        CsvImporter.Read(csv, profile, ctx =>
            Dec(ctx.Cells, "book_balance", out var bal)
                ? new BankBalanceRow(ctx.Cells["external_bank_id"], ctx.Cells["name"], bal)
                : ctx.Reject<BankBalanceRow>("book_balance", "not a number"));

    public static ImportResult<TenantReceivableRow> ReadTenantReceivables(Stream csv, ColumnMappingProfile profile) =>
        CsvImporter.Read(csv, profile, ctx =>
            Dec(ctx.Cells, "balance", out var bal)
                ? new TenantReceivableRow(ctx.Cells["external_tenant_id"], ctx.Cells["external_owner_id"], bal)
                : ctx.Reject<TenantReceivableRow>("balance", "not a number"));

    // Entity binders (Owners/Properties/Units/TenantsLeases) follow the same shape — implemented in WP-3
    // alongside their Directory-mapping use; each maps required string fields directly and parses
    // optional decimals/dates with Dec/Date helpers.

    private static bool Dec(IReadOnlyDictionary<string, string> c, string key, out decimal value) =>
        decimal.TryParse(c.GetValueOrDefault(key), NumberStyles.Currency, CultureInfo.InvariantCulture, out value);
}
