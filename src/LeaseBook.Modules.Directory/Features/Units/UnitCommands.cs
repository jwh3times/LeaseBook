using FluentValidation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Units;

public sealed record CreateUnit(Guid PropertyId, string Label, decimal Rent, string Status) : ICommand<Guid>;

public sealed record UpdateUnit(Guid Id, string Label, decimal Rent, string Status) : ICommand<bool>;

public sealed class CreateUnitValidator : AbstractValidator<CreateUnit>
{
    public CreateUnitValidator()
    {
        RuleFor(x => x.PropertyId).NotEmpty();
        RuleFor(x => x.Label).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Rent).MoneyAmount();
        RuleFor(x => x.Status).Must(v => UnitStatusConverter.DbValues.Contains(v))
            .WithMessage($"status must be one of: {string.Join(", ", UnitStatusConverter.DbValues)}.");
    }
}

public sealed class UpdateUnitValidator : AbstractValidator<UpdateUnit>
{
    public UpdateUnitValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Label).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Rent).MoneyAmount();
        RuleFor(x => x.Status).Must(v => UnitStatusConverter.DbValues.Contains(v))
            .WithMessage($"status must be one of: {string.Join(", ", UnitStatusConverter.DbValues)}.");
    }
}

internal sealed class CreateUnitHandler(DbContext db) : ICommandHandler<CreateUnit, Guid>
{
    public async Task<Guid> Handle(CreateUnit command, CancellationToken ct)
    {
        var unit = new Unit
        {
            Id = UuidV7.NewId(),
            PropertyId = command.PropertyId,
            Label = command.Label,
            Rent = new Money(command.Rent),
            Status = UnitStatusConverter.FromDb(command.Status),
        };
        db.Set<Unit>().Add(unit);
        await db.SaveChangesAsync(ct);
        return unit.Id;
    }
}

internal sealed class UpdateUnitHandler(DbContext db) : ICommandHandler<UpdateUnit, bool>
{
    public async Task<bool> Handle(UpdateUnit command, CancellationToken ct)
    {
        var unit = await db.Set<Unit>().FirstOrDefaultAsync(u => u.Id == command.Id && !u.IsSystem, ct);
        if (unit is null)
        {
            return false;
        }

        unit.Label = command.Label;
        unit.Rent = new Money(command.Rent);
        unit.Status = UnitStatusConverter.FromDb(command.Status);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
