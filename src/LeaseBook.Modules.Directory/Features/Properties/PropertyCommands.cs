using FluentValidation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Properties;

public sealed record CreateProperty(
    Guid OwnerId, string Address, string? City, string? State, string? Zip, int? MgmtFeeBps) : ICommand<Guid>;

public sealed record UpdateProperty(
    Guid Id, Guid OwnerId, string Address, string? City, string? State, string? Zip, int? MgmtFeeBps) : ICommand<bool>;

public sealed class CreatePropertyValidator : AbstractValidator<CreateProperty>
{
    public CreatePropertyValidator()
    {
        RuleFor(x => x.OwnerId).NotEmpty();
        RuleFor(x => x.Address).NotEmpty().MaximumLength(200);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.State).MaximumLength(50);
        RuleFor(x => x.Zip).MaximumLength(20);
        RuleFor(x => x.MgmtFeeBps).FeeBps();
    }
}

public sealed class UpdatePropertyValidator : AbstractValidator<UpdateProperty>
{
    public UpdatePropertyValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.OwnerId).NotEmpty();
        RuleFor(x => x.Address).NotEmpty().MaximumLength(200);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.State).MaximumLength(50);
        RuleFor(x => x.Zip).MaximumLength(20);
        RuleFor(x => x.MgmtFeeBps).FeeBps();
    }
}

internal sealed class CreatePropertyHandler(DbContext db) : ICommandHandler<CreateProperty, Guid>
{
    public async Task<Guid> Handle(CreateProperty command, CancellationToken ct)
    {
        var property = new Property
        {
            Id = UuidV7.NewId(),
            OwnerId = command.OwnerId,
            Address = command.Address,
            City = command.City,
            State = command.State,
            Zip = command.Zip,
            MgmtFeeBps = command.MgmtFeeBps,
        };
        db.Set<Property>().Add(property);
        await db.SaveChangesAsync(ct);
        return property.Id;
    }
}

internal sealed class UpdatePropertyHandler(DbContext db) : ICommandHandler<UpdateProperty, bool>
{
    public async Task<bool> Handle(UpdateProperty command, CancellationToken ct)
    {
        var property = await db.Set<Property>().FirstOrDefaultAsync(p => p.Id == command.Id && !p.IsSystem, ct);
        if (property is null)
        {
            return false;
        }

        property.OwnerId = command.OwnerId;
        property.Address = command.Address;
        property.City = command.City;
        property.State = command.State;
        property.Zip = command.Zip;
        property.MgmtFeeBps = command.MgmtFeeBps;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
