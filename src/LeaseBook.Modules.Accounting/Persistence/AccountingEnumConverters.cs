using LeaseBook.Modules.Accounting.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LeaseBook.Modules.Accounting.Persistence;

/// <summary>
/// Explicit enum &lt;-&gt; snake_case-text converters. Spelled out by hand (not algorithmically derived)
/// because the strings are a wire/storage contract: the DB CHECK constraints, the read-model SQL
/// (WP-06), and the seed/golden tests all encode these exact literals, and a silent rename would be a
/// correctness bug. <see cref="ToDb"/> is exposed so other module code can reference the canonical
/// text without re-deriving it.
/// </summary>
public sealed class AccountClassConverter() : ValueConverter<AccountClass, string>(v => ToDb(v), v => FromDb(v))
{
    public static string ToDb(AccountClass value) => value switch
    {
        AccountClass.TrustBank => "trust_bank",
        AccountClass.OwnerEquity => "owner_equity",
        AccountClass.TenantReceivable => "tenant_receivable",
        AccountClass.DepositLiability => "deposit_liability",
        AccountClass.PmIncome => "pm_income",
        AccountClass.PmOperatingBank => "pm_operating_bank",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown account class."),
    };

    public static AccountClass FromDb(string value) => value switch
    {
        "trust_bank" => AccountClass.TrustBank,
        "owner_equity" => AccountClass.OwnerEquity,
        "tenant_receivable" => AccountClass.TenantReceivable,
        "deposit_liability" => AccountClass.DepositLiability,
        "pm_income" => AccountClass.PmIncome,
        "pm_operating_bank" => AccountClass.PmOperatingBank,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown account class text."),
    };

    /// <summary>The CHECK-constraint set, single-quoted for inline SQL.</summary>
    public static readonly string[] DbValues =
    [
        "trust_bank", "owner_equity", "tenant_receivable", "deposit_liability", "pm_income", "pm_operating_bank",
    ];
}

public sealed class EntryBasisConverter() : ValueConverter<EntryBasis, string>(v => ToDb(v), v => FromDb(v))
{
    public static string ToDb(EntryBasis value) => value switch
    {
        EntryBasis.Cash => "cash",
        EntryBasis.Accrual => "accrual",
        EntryBasis.Both => "both",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown entry basis."),
    };

    public static EntryBasis FromDb(string value) => value switch
    {
        "cash" => EntryBasis.Cash,
        "accrual" => EntryBasis.Accrual,
        "both" => EntryBasis.Both,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown entry basis text."),
    };

    public static readonly string[] DbValues = ["cash", "accrual", "both"];
}

public sealed class PeriodStatusConverter() : ValueConverter<PeriodStatus, string>(v => ToDb(v), v => FromDb(v))
{
    public static string ToDb(PeriodStatus value) => value switch
    {
        PeriodStatus.Open => "open",
        PeriodStatus.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown period status."),
    };

    public static PeriodStatus FromDb(string value) => value switch
    {
        "open" => PeriodStatus.Open,
        "closed" => PeriodStatus.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown period status text."),
    };

    public static readonly string[] DbValues = ["open", "closed"];
}
