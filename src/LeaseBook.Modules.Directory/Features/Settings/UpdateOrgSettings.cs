using FluentValidation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Settings;

/// <summary>
/// Updates the org settings (§C.4, admin-only). Enums arrive as their snake_case text. Get-or-creates
/// the row, so the first write also initializes it.
/// </summary>
public sealed record UpdateOrgSettings(
    string AccountingBasis,
    string MoneyNegativeDisplay,
    string? LegalName,
    string? Address,
    string? City,
    string? State,
    string? Zip,
    string? Phone,
    string? LogoBlobRef) : ICommand<OrgSettingsResponse>;

public sealed class UpdateOrgSettingsValidator : AbstractValidator<UpdateOrgSettings>
{
    public UpdateOrgSettingsValidator()
    {
        RuleFor(x => x.AccountingBasis).Must(v => AccountingBasisConverter.DbValues.Contains(v))
            .WithMessage($"accountingBasis must be one of: {string.Join(", ", AccountingBasisConverter.DbValues)}.");
        RuleFor(x => x.MoneyNegativeDisplay).Must(v => MoneyNegativeDisplayConverter.DbValues.Contains(v))
            .WithMessage($"moneyNegativeDisplay must be one of: {string.Join(", ", MoneyNegativeDisplayConverter.DbValues)}.");
        RuleFor(x => x.LegalName).MaximumLength(200);
        RuleFor(x => x.Address).MaximumLength(200);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.State).MaximumLength(50);
        RuleFor(x => x.Zip).MaximumLength(20);
        RuleFor(x => x.Phone).MaximumLength(40);
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

        settings.AccountingBasis = AccountingBasisConverter.FromDb(command.AccountingBasis);
        settings.MoneyNegativeDisplay = MoneyNegativeDisplayConverter.FromDb(command.MoneyNegativeDisplay);
        settings.LegalName = command.LegalName;
        settings.Address = command.Address;
        settings.City = command.City;
        settings.State = command.State;
        settings.Zip = command.Zip;
        settings.Phone = command.Phone;
        settings.LogoBlobRef = command.LogoBlobRef;

        await db.SaveChangesAsync(ct);
        return OrgSettingsResponse.From(settings);
    }
}
