using System.Text.Json.Serialization;
using FluentValidation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Settings;

/// <summary>
/// Sets the convenience-fee rule for each online payment method (ADR-053, admin-only): a rate in basis
/// points, a fixed amount and an optional cap. The rule decides what a tenant is charged on top of the
/// amount going to their ledger, so this is a whole replacement, never a patch: both rates and both
/// fixed amounts are required, and a cap left out means no cap. A partial update could leave a rate
/// from one schedule beside a fixed amount from another.
/// <para>
/// These are the organization's own figures for what its processor charges. LeaseBook does not know a
/// processor's pricing, and the defaults charge nothing.
/// </para>
/// </summary>
// The rates and fixed amounts are nullable only so that leaving one out is a validation failure with
// a message, rather than a silent zero.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdatePaymentFeeSettings(
    int? CardFeeRateBps, decimal? CardFeeFixed, decimal? CardFeeCap,
    int? AchFeeRateBps, decimal? AchFeeFixed, decimal? AchFeeCap) : ICommand<OrgSettingsResponse>;

public sealed class UpdatePaymentFeeSettingsValidator : AbstractValidator<UpdatePaymentFeeSettings>
{
    /// <summary>20%. Input hygiene, far above any real rate; what matters is that it is below 100%.</summary>
    public const int MaxRateBps = 2_000;

    public const decimal MaxFixedFee = 100m;

    public const decimal MaxCap = 1_000m;

    public UpdatePaymentFeeSettingsValidator()
    {
        RuleFor(x => x.CardFeeRateBps).FeeRate("card");
        RuleFor(x => x.CardFeeFixed).FixedFee("card");
        RuleFor(x => x.CardFeeCap).FeeCap("card")
            .Must((x, cap) => cap is null || x.CardFeeFixed is null || cap >= x.CardFeeFixed)
            .WithMessage("The card fee cap cannot be below the card fixed fee.");

        RuleFor(x => x.AchFeeRateBps).FeeRate("bank debit");
        RuleFor(x => x.AchFeeFixed).FixedFee("bank debit");
        RuleFor(x => x.AchFeeCap).FeeCap("bank debit")
            .Must((x, cap) => cap is null || x.AchFeeFixed is null || cap >= x.AchFeeFixed)
            .WithMessage("The bank debit fee cap cannot be below the bank debit fixed fee.");
    }
}

internal static class PaymentFeeRules
{
    public static IRuleBuilderOptions<T, int?> FeeRate<T>(this IRuleBuilder<T, int?> rule, string method) =>
        rule.NotNull().WithMessage($"Enter the {method} fee rate.")
            .InclusiveBetween(0, UpdatePaymentFeeSettingsValidator.MaxRateBps)
            .WithMessage($"The {method} fee rate must be between 0 and "
                + $"{UpdatePaymentFeeSettingsValidator.MaxRateBps} basis points (0–20%).");

    public static IRuleBuilderOptions<T, decimal?> FixedFee<T>(this IRuleBuilder<T, decimal?> rule, string method) =>
        rule.NotNull().WithMessage($"Enter the {method} fixed fee.")
            .InclusiveBetween(0m, UpdatePaymentFeeSettingsValidator.MaxFixedFee)
            .WithMessage($"The {method} fixed fee must be between $0.00 and "
                + $"${UpdatePaymentFeeSettingsValidator.MaxFixedFee:0.00}.")
            .Must(WholeCents).WithMessage($"The {method} fixed fee must have at most 2 decimal places.");

    public static IRuleBuilderOptions<T, decimal?> FeeCap<T>(this IRuleBuilder<T, decimal?> rule, string method) =>
        rule.Must(cap => cap is null || (cap > 0m && cap <= UpdatePaymentFeeSettingsValidator.MaxCap))
            .WithMessage($"The {method} fee cap must be above $0.00 and at most "
                + $"${UpdatePaymentFeeSettingsValidator.MaxCap:0.00}, or left empty.")
            .Must(WholeCents).WithMessage($"The {method} fee cap must have at most 2 decimal places.");

    private static bool WholeCents(decimal? value) => value is null || decimal.Round(value.Value, 2) == value;
}

internal sealed class UpdatePaymentFeeSettingsHandler(DbContext db)
    : ICommandHandler<UpdatePaymentFeeSettings, OrgSettingsResponse>
{
    public async Task<OrgSettingsResponse> Handle(UpdatePaymentFeeSettings command, CancellationToken ct)
    {
        var settings = await db.Set<OrgSettings>().FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new OrgSettings { Id = UuidV7.NewId() };
            db.Add(settings);
        }
        // The validator has refused a missing rate or fixed amount before this runs.
        settings.CardFeeRateBps = command.CardFeeRateBps!.Value;
        settings.CardFeeFixed = command.CardFeeFixed!.Value;
        settings.CardFeeCap = command.CardFeeCap;
        settings.AchFeeRateBps = command.AchFeeRateBps!.Value;
        settings.AchFeeFixed = command.AchFeeFixed!.Value;
        settings.AchFeeCap = command.AchFeeCap;
        await db.SaveChangesAsync(ct);
        return OrgSettingsResponse.From(settings);
    }
}
