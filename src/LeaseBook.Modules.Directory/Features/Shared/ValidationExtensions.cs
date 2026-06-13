using FluentValidation;

namespace LeaseBook.Modules.Directory.Features.Shared;

/// <summary>Shared FluentValidation rules for directory command DTOs (validation is the single home, P23).</summary>
internal static class ValidationExtensions
{
    /// <summary>A non-negative money amount with at most 2 decimals (the NUMERIC(14,2) / Money gate, P28).</summary>
    public static IRuleBuilderOptions<T, decimal> MoneyAmount<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.GreaterThanOrEqualTo(0m).PrecisionScale(14, 2, ignoreTrailingZeros: true)
            .WithMessage("Must be a non-negative amount with at most 2 decimal places.");

    /// <summary>Management fee in basis points: 0..10000 (0–100%). Null (unset) passes.</summary>
    public static IRuleBuilderOptions<T, int?> FeeBps<T>(this IRuleBuilder<T, int?> rule) =>
        rule.InclusiveBetween(0, 10_000).WithMessage("Management fee must be between 0 and 10000 basis points.");
}
