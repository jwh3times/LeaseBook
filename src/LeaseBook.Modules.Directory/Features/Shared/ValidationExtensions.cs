using FluentValidation;
using LeaseBook.Modules.Directory.Domain;

namespace LeaseBook.Modules.Directory.Features.Shared;

/// <summary>Shared FluentValidation rules for directory command DTOs (validation is the single home, P23).</summary>
internal static class ValidationExtensions
{
    /// <summary>A non-negative money amount with at most 2 decimals (the NUMERIC(14,2) / Money gate, P28).</summary>
    public static IRuleBuilderOptions<T, decimal> MoneyAmount<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.GreaterThanOrEqualTo(0m).WithMessage("Must be a non-negative amount with at most 2 decimal places.")
            .PrecisionScale(14, 2, ignoreTrailingZeros: true)
            .WithMessage("Must be a non-negative amount with at most 2 decimal places.");

    /// <summary>Management fee in basis points: 0..10000 (0–100%). Null (unset) passes.</summary>
    public static IRuleBuilderOptions<T, int?> FeeBps<T>(this IRuleBuilder<T, int?> rule) =>
        rule.InclusiveBetween(0, 10_000).WithMessage("The management fee must be between 0 and 10000 basis points.");

    // ── Late-fee policy rules (WP-6) ─────────────────────────────────────────
    // The org defaults (OrgSettings) and the per-lease overrides (LeaseLite) are the same five
    // fields, so they share one set of rules. Keeping them here rather than inline is the point:
    // the two write paths had drifted apart, and an unvalidated override reached the late-fee run.
    // Every rule treats null as valid — for an override null means "inherit the org default", and
    // on the settings command it means "leave this field unchanged".

    /// <summary>Day of month rent is due: 1..28, so the day exists in every month. Null passes.</summary>
    public static IRuleBuilderOptions<T, int?> RentDueDayValue<T>(this IRuleBuilder<T, int?> rule) =>
        rule.InclusiveBetween(1, 28)
            .WithMessage("Rent due day must be between 1 and 28.");

    /// <summary>Days after the due date before a late fee applies. Zero is legal. Null passes.</summary>
    public static IRuleBuilderOptions<T, int?> LateFeeGraceDaysValue<T>(this IRuleBuilder<T, int?> rule) =>
        rule.GreaterThanOrEqualTo(0)
            .WithMessage("Grace days cannot be negative.");

    /// <summary>Late-fee kind, as its DB string. Null passes.</summary>
    public static IRuleBuilderOptions<T, string?> LateFeeKindValue<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(v => v is null || LateFeeKindConverter.DbValues.Contains(v))
            .WithMessage($"Late fee kind must be one of: {string.Join(", ", LateFeeKindConverter.DbValues)}.");

    /// <summary>Flat late fee in dollars. Zero is legal. Null passes.</summary>
    public static IRuleBuilderOptions<T, decimal?> LateFeeAmountValue<T>(this IRuleBuilder<T, decimal?> rule) =>
        rule.GreaterThanOrEqualTo(0m)
            .WithMessage("The late fee cannot be negative.")
            .PrecisionScale(14, 2, ignoreTrailingZeros: true)
            .WithMessage("The late fee must have at most 2 decimal places.");

    /// <summary>
    /// Late-fee rate in basis points: 0..10000 (0–100%). Null passes. The upper bound is input
    /// hygiene, not the fiduciary control — NC §42-46 is enforced downstream in
    /// <c>LateFeeCalculator</c>, which clamps the computed fee to the greater of $15 or 5% of rent
    /// regardless of what rate is configured. A rate above 100% of rent is nonsense input either way.
    /// </summary>
    public static IRuleBuilderOptions<T, int?> LateFeeRateBpsValue<T>(this IRuleBuilder<T, int?> rule) =>
        rule.InclusiveBetween(0, 10_000)
            .WithMessage("The late fee rate must be between 0 and 10000 basis points (0–100%).");
}
