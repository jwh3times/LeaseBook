using FluentValidation;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Payments.Features.RefundChecks;

/// <summary>A bank account's print calibration; zero offsets until one is saved.</summary>
public sealed record CheckPrintSettingsView(Guid BankAccountId, decimal OffsetXPoints, decimal OffsetYPoints);

public sealed record GetCheckPrintSettings(Guid BankAccountId) : IQuery<CheckPrintSettingsView>;

internal sealed class GetCheckPrintSettingsHandler(DbContext db) : IQueryHandler<GetCheckPrintSettings, CheckPrintSettingsView>
{
    public async Task<CheckPrintSettingsView> Handle(GetCheckPrintSettings q, CancellationToken ct)
    {
        var row = await db.Set<CheckPrintSetting>().AsNoTracking().FirstOrDefaultAsync(x => x.BankAccountId == q.BankAccountId, ct);
        return new CheckPrintSettingsView(q.BankAccountId, row?.OffsetXPoints ?? 0m, row?.OffsetYPoints ?? 0m);
    }
}

/// <summary>
/// Saves a bank account's print offsets (#473), bounded to one inch either way. The offsets shift the
/// printed fields only; they never touch money or the ledger.
/// </summary>
public sealed record SaveCheckPrintSettings(Guid BankAccountId, decimal OffsetXPoints, decimal OffsetYPoints)
    : ICommand<CheckPrintSettingsView>;

public sealed class SaveCheckPrintSettingsValidator : AbstractValidator<SaveCheckPrintSettings>
{
    public SaveCheckPrintSettingsValidator()
    {
        RuleFor(x => x.BankAccountId).NotEmpty();
        RuleFor(x => x.OffsetXPoints).InclusiveBetween(-72m, 72m).Must(v => decimal.Round(v, 2) == v);
        RuleFor(x => x.OffsetYPoints).InclusiveBetween(-72m, 72m).Must(v => decimal.Round(v, 2) == v);
    }
}

internal sealed class SaveCheckPrintSettingsHandler(DbContext db, TimeProvider clock)
    : ICommandHandler<SaveCheckPrintSettings, CheckPrintSettingsView>
{
    public async Task<CheckPrintSettingsView> Handle(SaveCheckPrintSettings c, CancellationToken ct)
    {
        var row = await db.Set<CheckPrintSetting>().FirstOrDefaultAsync(x => x.BankAccountId == c.BankAccountId, ct);
        if (row is null)
        {
            row = new CheckPrintSetting { Id = UuidV7.NewId(), BankAccountId = c.BankAccountId };
            db.Add(row);
        }

        row.OffsetXPoints = c.OffsetXPoints;
        row.OffsetYPoints = c.OffsetYPoints;
        row.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return new CheckPrintSettingsView(c.BankAccountId, row.OffsetXPoints, row.OffsetYPoints);
    }
}
