using LeaseBook.Modules.Directory.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LeaseBook.Modules.Directory.Persistence;

/// <summary>
/// Explicit enum &lt;-&gt; snake_case-text converters for the directory enums. Spelled out by hand (not
/// algorithmically derived) because the strings are a storage/wire contract: the DB CHECK constraints,
/// the search/list SQL, and the seed/golden tests all encode these exact literals, and a silent rename
/// would be a correctness bug. <see cref="DbValues"/> on each feeds the CHECK constraint set; <c>ToDb</c>
/// is exposed so other module code can reference the canonical text without re-deriving it. (Mirrors
/// <c>AccountingEnumConverters</c> — Directory cannot reference the Accounting assembly, ADR-007.)
/// </summary>
public sealed class UnitAvailabilityConverter()
    : ValueConverter<UnitAvailability, string>(v => ToDb(v), v => FromDb(v))
{
    public static string ToDb(UnitAvailability value) => value switch
    {
        UnitAvailability.Available => "available",
        UnitAvailability.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown unit availability."),
    };

    public static UnitAvailability FromDb(string value) => value switch
    {
        "available" => UnitAvailability.Available,
        "unavailable" => UnitAvailability.Unavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown unit availability text."),
    };

    public static readonly string[] DbValues = ["available", "unavailable"];
}

public sealed class TenantLifecycleStatusConverter()
    : ValueConverter<TenantLifecycleStatus, string>(v => ToDb(v), v => FromDb(v))
{
    public static string ToDb(TenantLifecycleStatus value) => value switch
    {
        TenantLifecycleStatus.Current => "current",
        TenantLifecycleStatus.Evicting => "evicting",
        TenantLifecycleStatus.Past => "past",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown tenant lifecycle status."),
    };

    public static TenantLifecycleStatus FromDb(string value) => value switch
    {
        "current" => TenantLifecycleStatus.Current,
        "evicting" => TenantLifecycleStatus.Evicting,
        "past" => TenantLifecycleStatus.Past,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown tenant lifecycle status text."),
    };

    public static readonly string[] DbValues = ["current", "evicting", "past"];
}

public sealed class LeaseStatusConverter() : ValueConverter<LeaseStatus, string>(v => ToDb(v), v => FromDb(v))
{
    public static string ToDb(LeaseStatus value) => value switch
    {
        LeaseStatus.Active => "active",
        LeaseStatus.Ended => "ended",
        LeaseStatus.Pending => "pending",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown lease status."),
    };

    public static LeaseStatus FromDb(string value) => value switch
    {
        "active" => LeaseStatus.Active,
        "ended" => LeaseStatus.Ended,
        "pending" => LeaseStatus.Pending,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown lease status text."),
    };

    public static readonly string[] DbValues = ["active", "ended", "pending"];
}

public sealed class BankPurposeConverter() : ValueConverter<BankPurpose, string>(v => ToDb(v), v => FromDb(v))
{
    public static string ToDb(BankPurpose value) => value switch
    {
        BankPurpose.Trust => "trust",
        BankPurpose.Deposit => "deposit",
        BankPurpose.Operating => "operating",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown bank purpose."),
    };

    public static BankPurpose FromDb(string value) => value switch
    {
        "trust" => BankPurpose.Trust,
        "deposit" => BankPurpose.Deposit,
        "operating" => BankPurpose.Operating,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown bank purpose text."),
    };

    public static readonly string[] DbValues = ["trust", "deposit", "operating"];
}

public sealed class AccountingBasisConverter() : ValueConverter<AccountingBasis, string>(v => ToDb(v), v => FromDb(v))
{
    public static string ToDb(AccountingBasis value) => value switch
    {
        AccountingBasis.Cash => "cash",
        AccountingBasis.Accrual => "accrual",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown accounting basis."),
    };

    public static AccountingBasis FromDb(string value) => value switch
    {
        "cash" => AccountingBasis.Cash,
        "accrual" => AccountingBasis.Accrual,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown accounting basis text."),
    };

    public static readonly string[] DbValues = ["cash", "accrual"];
}

public sealed class MoneyNegativeDisplayConverter()
    : ValueConverter<MoneyNegativeDisplay, string>(v => ToDb(v), v => FromDb(v))
{
    public static string ToDb(MoneyNegativeDisplay value) => value switch
    {
        MoneyNegativeDisplay.Minus => "minus",
        MoneyNegativeDisplay.Parens => "parens",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown money negative display."),
    };

    public static MoneyNegativeDisplay FromDb(string value) => value switch
    {
        "minus" => MoneyNegativeDisplay.Minus,
        "parens" => MoneyNegativeDisplay.Parens,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown money negative display text."),
    };

    public static readonly string[] DbValues = ["minus", "parens"];
}
