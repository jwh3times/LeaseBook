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

    // Entity binders — WP-3 Task 3.1.

    public static ImportResult<OwnerRow> ReadOwners(Stream csv, ColumnMappingProfile profile) =>
        CsvImporter.Read(csv, profile, ctx =>
        {
            var name = ctx.Cells.GetValueOrDefault("name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                return ctx.Reject<OwnerRow>("name", "required");
            var externalId = ctx.Cells.GetValueOrDefault("external_id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(externalId))
                return ctx.Reject<OwnerRow>("external_id", "required");
            Dec(ctx.Cells, "reserve", out var reserve); // optional — default 0
            return new OwnerRow(externalId, name, reserve);
        });

    public static ImportResult<PropertyRow> ReadProperties(Stream csv, ColumnMappingProfile profile) =>
        CsvImporter.Read(csv, profile, ctx =>
        {
            var externalId = ctx.Cells.GetValueOrDefault("external_id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(externalId))
                return ctx.Reject<PropertyRow>("external_id", "required");
            var externalOwnerId = ctx.Cells.GetValueOrDefault("external_owner_id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(externalOwnerId))
                return ctx.Reject<PropertyRow>("external_owner_id", "required");
            var address = ctx.Cells.GetValueOrDefault("address") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(address))
                return ctx.Reject<PropertyRow>("address", "required");
            return new PropertyRow(externalId, externalOwnerId, address);
        });

    public static ImportResult<UnitRow> ReadUnits(Stream csv, ColumnMappingProfile profile) =>
        CsvImporter.Read(csv, profile, ctx =>
        {
            var externalId = ctx.Cells.GetValueOrDefault("external_id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(externalId))
                return ctx.Reject<UnitRow>("external_id", "required");
            var externalPropertyId = ctx.Cells.GetValueOrDefault("external_property_id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(externalPropertyId))
                return ctx.Reject<UnitRow>("external_property_id", "required");
            var label = ctx.Cells.GetValueOrDefault("label") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(label))
                return ctx.Reject<UnitRow>("label", "required");
            Dec(ctx.Cells, "rent", out var rent); // optional
            var status = ctx.Cells.GetValueOrDefault("status") ?? "vacant"; // optional, default vacant
            return new UnitRow(externalId, externalPropertyId, label, rent, status);
        });

    public static ImportResult<TenantLeaseRow> ReadTenantsLeases(Stream csv, ColumnMappingProfile profile) =>
        CsvImporter.Read(csv, profile, ctx =>
        {
            var externalId = ctx.Cells.GetValueOrDefault("external_id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(externalId))
                return ctx.Reject<TenantLeaseRow>("external_id", "required");
            var externalUnitId = ctx.Cells.GetValueOrDefault("external_unit_id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(externalUnitId))
                return ctx.Reject<TenantLeaseRow>("external_unit_id", "required");
            var name = ctx.Cells.GetValueOrDefault("name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                return ctx.Reject<TenantLeaseRow>("name", "required");
            var start = Date(ctx.Cells, "start");
            var end = Date(ctx.Cells, "end");
            Dec(ctx.Cells, "rent", out var rent);
            Dec(ctx.Cells, "deposit", out var deposit);
            var status = ctx.Cells.GetValueOrDefault("status") ?? "active";
            return new TenantLeaseRow(externalId, externalUnitId, name, start, end, rent, deposit, status);
        });

    private static bool Dec(IReadOnlyDictionary<string, string> c, string key, out decimal value) =>
        decimal.TryParse(c.GetValueOrDefault(key), NumberStyles.Currency, CultureInfo.InvariantCulture, out value);

    private static DateOnly? Date(IReadOnlyDictionary<string, string> c, string key)
    {
        var raw = c.GetValueOrDefault(key);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateOnly.TryParse(raw, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
