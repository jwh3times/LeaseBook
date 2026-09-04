using System.Globalization;
using LeaseBook.Migrator.Csv;
using LeaseBook.Migrator.Model;

namespace LeaseBook.Migrator;

/// <summary>The host workflow family that consumes an AppFolio import definition.</summary>
public enum AppFolioImportFamily
{
    Entity,
    Balance,
}

/// <summary>
/// AppFolio-owned metadata shared by typed import definitions. The definition instance is the
/// in-process kind identity; <see cref="PersistedName"/> is its stable database and audit identity.
/// </summary>
public abstract class AppFolioImportDefinition
{
    private protected AppFolioImportDefinition(
        string canonicalToken,
        string persistedName,
        AppFolioImportFamily family,
        string profileId,
        ColumnMappingProfile profile)
    {
        CanonicalToken = canonicalToken;
        PersistedName = persistedName;
        Family = family;
        ProfileId = profileId;
        Profile = profile;
    }

    public string CanonicalToken { get; }
    public string PersistedName { get; }
    public AppFolioImportFamily Family { get; }
    public string ProfileId { get; }
    public ColumnMappingProfile Profile { get; }
}

/// <summary>An AppFolio import definition whose CSV rows bind to <typeparamref name="TRow"/>.</summary>
public sealed class AppFolioImportDefinition<TRow> : AppFolioImportDefinition
    where TRow : class
{
    private readonly Func<RowContext, TRow?> _bind;

    internal AppFolioImportDefinition(
        string canonicalToken,
        string persistedName,
        AppFolioImportFamily family,
        string profileId,
        ColumnMappingProfile profile,
        Func<RowContext, TRow?> bind)
        : base(canonicalToken, persistedName, family, profileId, profile)
    {
        _bind = bind;
    }

    public ImportResult<TRow> Read(Stream csv) => CsvImporter.Read(csv, Profile, _bind);
}

/// <summary>
/// Executable catalog for the AppFolio CSV dialect. A definition owns the route token, workflow
/// family, persisted identity, profile provenance, required columns, and typed row binder.
/// </summary>
public static class AppFolioImportCatalog
{
    private const string DefaultProfileId = "appfolio-default";
    private static readonly List<AppFolioImportDefinition> Definitions = [];

    public static AppFolioImportDefinition<OwnerRow> Owners { get; } = Define(
        "owners",
        "Owners",
        AppFolioImportFamily.Entity,
        [
            new("external_id", ["Owner ID", "ID"], Required: true),
            new("name", ["Owner Name", "Name"], Required: true),
            new("reserve", ["Reserve", "Reserve Amount"], Required: false),
        ],
        ctx =>
        {
            var name = ctx.Cells.GetValueOrDefault("name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                return ctx.Reject<OwnerRow>("name", "required");
            var externalId = ctx.Cells.GetValueOrDefault("external_id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(externalId))
                return ctx.Reject<OwnerRow>("external_id", "required");
            if (!OptionalDecimal(ctx.Cells, "reserve", out var reserve))
                return ctx.Reject<OwnerRow>("reserve", "not a number");
            return new OwnerRow(externalId, name, reserve);
        });

    public static AppFolioImportDefinition<PropertyRow> Properties { get; } = Define(
        "properties",
        "Properties",
        AppFolioImportFamily.Entity,
        [
            new("external_id", ["Property ID", "ID"], Required: true),
            new("external_owner_id", ["Owner ID"], Required: true),
            new("address", ["Address", "Property Address"], Required: true),
        ],
        ctx =>
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

    public static AppFolioImportDefinition<UnitRow> Units { get; } = Define(
        "units",
        "Units",
        AppFolioImportFamily.Entity,
        [
            new("external_id", ["Unit ID", "ID"], Required: true),
            new("external_property_id", ["Property ID"], Required: true),
            new("label", ["Unit", "Unit Name", "Label"], Required: true),
            new("rent", ["Market Rent", "Rent"], Required: false),
            new("status", ["Status"], Required: false),
        ],
        ctx =>
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
            if (!OptionalDecimal(ctx.Cells, "rent", out var rent))
                return ctx.Reject<UnitRow>("rent", "not a number");
            var status = ctx.Cells.GetValueOrDefault("status") ?? "vacant";
            return new UnitRow(externalId, externalPropertyId, label, rent, status);
        });

    public static AppFolioImportDefinition<TenantLeaseRow> TenantsLeases { get; } = Define(
        "tenants_leases",
        "TenantsLeases",
        AppFolioImportFamily.Entity,
        [
            new("external_id", ["Tenant ID", "Lease ID", "ID"], Required: true),
            new("external_unit_id", ["Unit ID"], Required: true),
            new("name", ["Tenant Name", "Name"], Required: true),
            new("start", ["Lease Start", "Start"], Required: false),
            new("end", ["Lease End", "End"], Required: false),
            new("rent", ["Rent"], Required: false),
            new("deposit", ["Deposit", "Deposit Required"], Required: false),
            new("status", ["Status"], Required: false),
        ],
        ctx =>
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
            if (!OptionalDate(ctx.Cells, "start", out var start))
                return ctx.Reject<TenantLeaseRow>("start", "not a date");
            if (!OptionalDate(ctx.Cells, "end", out var end))
                return ctx.Reject<TenantLeaseRow>("end", "not a date");
            if (!OptionalDecimal(ctx.Cells, "rent", out var rent))
                return ctx.Reject<TenantLeaseRow>("rent", "not a number");
            if (!OptionalDecimal(ctx.Cells, "deposit", out var deposit))
                return ctx.Reject<TenantLeaseRow>("deposit", "not a number");
            var status = ctx.Cells.GetValueOrDefault("status") ?? "active";
            return new TenantLeaseRow(externalId, externalUnitId, name, start, end, rent, deposit, status);
        });

    public static AppFolioImportDefinition<OwnerBalanceRow> OwnerBalances { get; } = Define(
        "owner_balances",
        "OwnerBalances",
        AppFolioImportFamily.Balance,
        [
            new("external_owner_id", ["Owner ID", "Owner Id", "ID"], Required: true),
            new("name", ["Owner Name", "Name"], Required: true),
            new("cash_balance", ["Cash Balance", "Cash"], Required: true),
            new("accrual_balance", ["Accrual Balance", "Accrual"], Required: true),
        ],
        ctx =>
        {
            if (!Decimal(ctx.Cells, "cash_balance", out var cash))
                return ctx.Reject<OwnerBalanceRow>("cash_balance", "not a number");
            if (!Decimal(ctx.Cells, "accrual_balance", out var accrual))
                return ctx.Reject<OwnerBalanceRow>("accrual_balance", "not a number");
            return new OwnerBalanceRow(ctx.Cells["external_owner_id"], ctx.Cells["name"], cash, accrual);
        });

    public static AppFolioImportDefinition<DepositLiabilityRow> DepositLiabilities { get; } = Define(
        "deposit_liabilities",
        "DepositLiabilities",
        AppFolioImportFamily.Balance,
        [
            new("external_tenant_id", ["Tenant ID", "Tenant Id"], Required: true),
            new("external_owner_id", ["Owner ID", "Owner Id"], Required: true),
            new("held_amount", ["Deposit Held", "Held", "Amount"], Required: true),
        ],
        ctx => Decimal(ctx.Cells, "held_amount", out var held)
            ? new DepositLiabilityRow(ctx.Cells["external_tenant_id"], ctx.Cells["external_owner_id"], held)
            : ctx.Reject<DepositLiabilityRow>("held_amount", "not a number"));

    public static AppFolioImportDefinition<BankBalanceRow> BankBalances { get; } = Define(
        "bank_balances",
        "BankBalances",
        AppFolioImportFamily.Balance,
        [
            new("external_bank_id", ["Account ID", "Bank Account", "Account"], Required: true),
            new("name", ["Account Name", "Name"], Required: true),
            new("book_balance", ["Book Balance", "Balance"], Required: true),
        ],
        ctx => Decimal(ctx.Cells, "book_balance", out var balance)
            ? new BankBalanceRow(ctx.Cells["external_bank_id"], ctx.Cells["name"], balance)
            : ctx.Reject<BankBalanceRow>("book_balance", "not a number"));

    public static AppFolioImportDefinition<TenantReceivableRow> TenantReceivables { get; } = Define(
        "tenant_receivables",
        "TenantReceivables",
        AppFolioImportFamily.Balance,
        [
            new("external_tenant_id", ["Tenant ID", "Tenant Id"], Required: true),
            new("external_owner_id", ["Owner ID", "Owner Id"], Required: true),
            new("balance", ["Balance Due", "Receivable", "Balance"], Required: true),
        ],
        ctx => Decimal(ctx.Cells, "balance", out var balance)
            ? new TenantReceivableRow(ctx.Cells["external_tenant_id"], ctx.Cells["external_owner_id"], balance)
            : ctx.Reject<TenantReceivableRow>("balance", "not a number"));

    public static AppFolioImportDefinition<HeldPmFeeRow> HeldPmFees { get; } = Define(
        "held_pm_fees",
        "HeldPmFees",
        AppFolioImportFamily.Balance,
        [
            new("external_bank_id", ["Account ID", "Bank Account", "Account"], Required: true),
            new("name", ["Account Name", "Name"], Required: true),
            new("held_amount", ["Held Fees", "Unremitted Fees", "Fees Held", "Amount"], Required: true),
        ],
        ctx => Decimal(ctx.Cells, "held_amount", out var held)
            ? new HeldPmFeeRow(ctx.Cells["external_bank_id"], ctx.Cells["name"], held)
            : ctx.Reject<HeldPmFeeRow>("held_amount", "not a number"));

    public static IReadOnlyList<AppFolioImportDefinition> All { get; }

    private static readonly IReadOnlyDictionary<string, AppFolioImportDefinition> ByToken;

    static AppFolioImportCatalog()
    {
        All = Array.AsReadOnly(Definitions.ToArray());

        EnsureUnique(All, definition => definition.PersistedName, "persisted name", StringComparer.Ordinal);
        EnsureUnique(All, definition => definition.CanonicalToken, "canonical token", StringComparer.Ordinal);
        EnsureUnique(All, definition => NormaliseToken(definition.CanonicalToken), "lookup token", StringComparer.OrdinalIgnoreCase);
        ByToken = All.ToDictionary(
            definition => NormaliseToken(definition.CanonicalToken),
            StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<AppFolioImportDefinition> ForFamily(AppFolioImportFamily family) =>
        All.Where(definition => definition.Family == family).ToArray();

    public static bool TryResolve(
        string token,
        AppFolioImportFamily family,
        out AppFolioImportDefinition definition)
    {
        if (ByToken.TryGetValue(NormaliseToken(token), out var candidate) && candidate.Family == family)
        {
            definition = candidate;
            return true;
        }

        definition = null!;
        return false;
    }

    private static AppFolioImportDefinition<TRow> Define<TRow>(
        string canonicalToken,
        string persistedName,
        AppFolioImportFamily family,
        IReadOnlyList<FieldMapping> fields,
        Func<RowContext, TRow?> bind)
        where TRow : class
    {
        var definition = new AppFolioImportDefinition<TRow>(
            canonicalToken,
            persistedName,
            family,
            DefaultProfileId,
            new ColumnMappingProfile(fields),
            bind);
        Definitions.Add(definition);
        return definition;
    }

    private static string NormaliseToken(string token) => token.Replace("_", string.Empty);

    private static void EnsureUnique(
        IEnumerable<AppFolioImportDefinition> definitions,
        Func<AppFolioImportDefinition, string> key,
        string label,
        IEqualityComparer<string> comparer)
    {
        var duplicate = definitions.GroupBy(key, comparer).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate AppFolio import {label}: {duplicate.Key}");
    }

    private static bool Decimal(IReadOnlyDictionary<string, string> cells, string key, out decimal value) =>
        decimal.TryParse(cells.GetValueOrDefault(key), NumberStyles.Currency, CultureInfo.InvariantCulture, out value);

    private static bool OptionalDecimal(
        IReadOnlyDictionary<string, string> cells,
        string key,
        out decimal value)
    {
        var raw = cells.GetValueOrDefault(key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = 0m;
            return true;
        }

        return decimal.TryParse(raw, NumberStyles.Currency, CultureInfo.InvariantCulture, out value);
    }

    private static bool OptionalDate(
        IReadOnlyDictionary<string, string> cells,
        string key,
        out DateOnly? value)
    {
        var raw = cells.GetValueOrDefault(key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = null;
            return true;
        }

        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }
}
