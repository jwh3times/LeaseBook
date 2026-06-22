using FluentValidation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Owners;

/// <summary>Create an owner (§C.3). Returns the new id. No delete in M2.</summary>
public sealed record CreateOwner(
    string Name, string? Initials, string? ContactEmail, string? ContactPhone,
    int? DefaultMgmtFeeBps, decimal ReserveAmount) : ICommand<Guid>;

/// <summary>Update an owner (§C.3). Returns false → 404 when the id is unknown / system.</summary>
public sealed record UpdateOwner(
    Guid Id, string Name, string? Initials, string? ContactEmail, string? ContactPhone,
    int? DefaultMgmtFeeBps, decimal ReserveAmount) : ICommand<bool>;

public sealed class CreateOwnerValidator : AbstractValidator<CreateOwner>
{
    public CreateOwnerValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Initials).MaximumLength(8);
        RuleFor(x => x.ContactEmail).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.ContactEmail));
        RuleFor(x => x.DefaultMgmtFeeBps).FeeBps();
        RuleFor(x => x.ReserveAmount).MoneyAmount();
    }
}

public sealed class UpdateOwnerValidator : AbstractValidator<UpdateOwner>
{
    public UpdateOwnerValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Initials).MaximumLength(8);
        RuleFor(x => x.ContactEmail).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.ContactEmail));
        RuleFor(x => x.DefaultMgmtFeeBps).FeeBps();
        RuleFor(x => x.ReserveAmount).MoneyAmount();
    }
}

internal sealed class CreateOwnerHandler(DbContext db) : ICommandHandler<CreateOwner, Guid>
{
    public async Task<Guid> Handle(CreateOwner command, CancellationToken ct)
    {
        var owner = new Owner
        {
            Id = UuidV7.NewId(),
            Name = command.Name,
            Initials = command.Initials,
            ContactEmail = command.ContactEmail,
            ContactPhone = command.ContactPhone,
            DefaultMgmtFeeBps = command.DefaultMgmtFeeBps,
            ReserveAmount = new Money(command.ReserveAmount),
        };
        db.Set<Owner>().Add(owner);
        await db.SaveChangesAsync(ct);
        return owner.Id;
    }
}

internal sealed class UpdateOwnerHandler(DbContext db) : ICommandHandler<UpdateOwner, bool>
{
    public async Task<bool> Handle(UpdateOwner command, CancellationToken ct)
    {
        var owner = await db.Set<Owner>().NotSystem().FirstOrDefaultAsync(o => o.Id == command.Id, ct);
        if (owner is null)
        {
            return false;
        }

        owner.Name = command.Name;
        owner.Initials = command.Initials;
        owner.ContactEmail = command.ContactEmail;
        owner.ContactPhone = command.ContactPhone;
        owner.DefaultMgmtFeeBps = command.DefaultMgmtFeeBps;
        owner.ReserveAmount = new Money(command.ReserveAmount);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
