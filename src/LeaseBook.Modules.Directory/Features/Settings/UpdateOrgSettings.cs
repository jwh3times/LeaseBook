using FluentValidation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Settings;

/// <summary>
/// Updates the org settings (§C.4, admin-only). Enums arrive as their snake_case text. Get-or-creates
/// the row, so the first write also initializes it. Late-fee fields (WP-3) are optional; null means
/// "keep existing value".
/// </summary>
public sealed record UpdateOrgSettings(
    string? AccountingBasis,
    string? MoneyNegativeDisplay,
    string? LegalName,
    string? Address,
    string? City,
    string? State,
    string? Zip,
    string? Phone,
    string? LogoBlobRef,
    // Late-fee org defaults (WP-3 / NC §42-46). Optional — null = keep current value.
    int? RentDueDay = null,
    int? LateFeeGraceDays = null,
    string? LateFeeKind = null,
    decimal? LateFeeAmount = null,
    int? LateFeeRateBps = null) : ICommand<OrgSettingsResponse>;

public sealed class UpdateOrgSettingsValidator : AbstractValidator<UpdateOrgSettings>
{
    public UpdateOrgSettingsValidator()
    {
        RuleFor(x => x.AccountingBasis)
            .Must(v => v is null || AccountingBasisConverter.DbValues.Contains(v))
            .WithMessage($"accountingBasis must be one of: {string.Join(", ", AccountingBasisConverter.DbValues)}.");
        RuleFor(x => x.MoneyNegativeDisplay)
            .Must(v => v is null || MoneyNegativeDisplayConverter.DbValues.Contains(v))
            .WithMessage($"moneyNegativeDisplay must be one of: {string.Join(", ", MoneyNegativeDisplayConverter.DbValues)}.");
        RuleFor(x => x.LegalName).MaximumLength(200);
        RuleFor(x => x.Address).MaximumLength(200);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.State).MaximumLength(50);
        RuleFor(x => x.Zip).MaximumLength(20);
        RuleFor(x => x.Phone).MaximumLength(40);
        RuleFor(x => x.RentDueDay).InclusiveBetween(1, 28).When(x => x.RentDueDay.HasValue);
        RuleFor(x => x.LateFeeGraceDays).GreaterThanOrEqualTo(0).When(x => x.LateFeeGraceDays.HasValue);
        RuleFor(x => x.LateFeeKind)
            .Must(v => v is null || LateFeeKindConverter.DbValues.Contains(v))
            .WithMessage($"lateFeeKind must be one of: {string.Join(", ", LateFeeKindConverter.DbValues)}.");
        RuleFor(x => x.LateFeeAmount).GreaterThanOrEqualTo(0m).When(x => x.LateFeeAmount.HasValue);
        RuleFor(x => x.LateFeeRateBps).GreaterThanOrEqualTo(0).When(x => x.LateFeeRateBps.HasValue);
    }
}

internal sealed class UpdateOrgSettingsHandler(DbContext db) : ICommandHandler<UpdateOrgSettings, OrgSettingsResponse>
{
    public async Task<OrgSettingsResponse> Handle(UpdateOrgSettings command, CancellationToken ct)
    {
        var settings = await db.Set<OrgSettings>().FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new OrgSettings { Id = UuidV7.NewId() };
            db.Set<OrgSettings>().Add(settings);
        }

        if (command.AccountingBasis is not null)
            settings.AccountingBasis = AccountingBasisConverter.FromDb(command.AccountingBasis);
        if (command.MoneyNegativeDisplay is not null)
            settings.MoneyNegativeDisplay = MoneyNegativeDisplayConverter.FromDb(command.MoneyNegativeDisplay);
        settings.LegalName = command.LegalName;
        settings.Address = command.Address;
        settings.City = command.City;
        settings.State = command.State;
        settings.Zip = command.Zip;
        settings.Phone = command.Phone;
        settings.LogoBlobRef = command.LogoBlobRef;

        // Late-fee org defaults — only update if explicitly provided (null = keep existing).
        if (command.RentDueDay.HasValue) settings.RentDueDay = command.RentDueDay.Value;
        if (command.LateFeeGraceDays.HasValue) settings.LateFeeGraceDays = command.LateFeeGraceDays.Value;
        if (command.LateFeeKind is not null) settings.LateFeeKind = LateFeeKindConverter.FromDb(command.LateFeeKind);
        if (command.LateFeeAmount.HasValue) settings.LateFeeAmount = command.LateFeeAmount.Value;
        if (command.LateFeeRateBps.HasValue) settings.LateFeeRateBps = command.LateFeeRateBps.Value;

        await db.SaveChangesAsync(ct);
        return OrgSettingsResponse.From(settings);
    }
}
