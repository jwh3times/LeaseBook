using FluentValidation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
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
            .WithMessage($"Accounting basis must be one of: {string.Join(", ", AccountingBasisConverter.DbValues)}.");
        RuleFor(x => x.MoneyNegativeDisplay)
            .Must(v => v is null || MoneyNegativeDisplayConverter.DbValues.Contains(v))
            .WithMessage($"Negative amount display must be one of: {string.Join(", ", MoneyNegativeDisplayConverter.DbValues)}.");
        RuleFor(x => x.LegalName).MaximumLength(200);
        RuleFor(x => x.Address).MaximumLength(200);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.State).MaximumLength(50);
        RuleFor(x => x.Zip).MaximumLength(20);
        RuleFor(x => x.Phone).MaximumLength(40);
        // Late-fee org defaults — the same five rules the per-lease overrides use (WP-6). Shared so
        // the two write paths cannot drift again; the override path previously had none of them.
        // Note this tightened LateFeeRateBps, which was bounded below but not above: 0..10000 now.
        RuleFor(x => x.RentDueDay).RentDueDayValue();
        RuleFor(x => x.LateFeeGraceDays).LateFeeGraceDaysValue();
        RuleFor(x => x.LateFeeKind).LateFeeKindValue();
        RuleFor(x => x.LateFeeAmount).LateFeeAmountValue();
        RuleFor(x => x.LateFeeRateBps).LateFeeRateBpsValue();
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
