using FluentValidation;
using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Properties;

public sealed record CreateProperty(
    Guid OwnerId, string Address, string? City, string? State, string? Zip, int? MgmtFeeBps) : ICommand<Guid>;

public sealed record UpdateProperty(
    Guid Id, string Address, string? City, string? State, string? Zip, int? MgmtFeeBps) : ICommand<bool>;

public sealed record TransferPropertyOwnership(
    Guid PropertyId, Guid ToOwnerId, DateOnly EffectiveDate, string SourceRef)
    : ICommand<PropertyOwnershipTransferResult?>;

public sealed record PropertyOwnershipTransferResult(
    Guid PropertyId, Guid FromOwnerId, Guid ToOwnerId, DateOnly EffectiveDate, Guid? DepositEntryId);

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
        RuleFor(x => x.Address).NotEmpty().MaximumLength(200);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.State).MaximumLength(50);
        RuleFor(x => x.Zip).MaximumLength(20);
        RuleFor(x => x.MgmtFeeBps).FeeBps();
    }
}

public sealed class TransferPropertyOwnershipValidator : AbstractValidator<TransferPropertyOwnership>
{
    public TransferPropertyOwnershipValidator()
    {
        RuleFor(command => command.PropertyId).NotEmpty();
        RuleFor(command => command.ToOwnerId).NotEmpty();
        RuleFor(command => command.EffectiveDate)
            .NotEmpty()
            .Must(effectiveDate => effectiveDate <= DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("A property ownership transfer cannot be scheduled for a future date.");
        RuleFor(command => command.SourceRef).NotEmpty().MaximumLength(200);
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
        var property = await db.Set<Property>().NotSystem().FirstOrDefaultAsync(p => p.Id == command.Id, ct);
        if (property is null)
        {
            return false;
        }

        property.Address = command.Address;
        property.City = command.City;
        property.State = command.State;
        property.Zip = command.Zip;
        property.MgmtFeeBps = command.MgmtFeeBps;
        await db.SaveChangesAsync(ct);
        return true;
    }
}

internal sealed class TransferPropertyOwnershipHandler(DbContext db, IPropertyDepositTransfer depositTransfer)
    : ICommandHandler<TransferPropertyOwnership, PropertyOwnershipTransferResult?>
{
    public async Task<PropertyOwnershipTransferResult?> Handle(
        TransferPropertyOwnership command,
        CancellationToken ct)
    {
        // Serialize ownership changes for this property. The lock lives to the end of the ambient
        // org transaction, so a competing transfer cannot read a stale current owner and append a
        // forked history row.
        var lockedPropertyId = await db.Database.SqlQuery<Guid>(
                $"""SELECT id AS "Value" FROM properties WHERE id = {command.PropertyId} AND NOT is_system FOR UPDATE""")
            .SingleOrDefaultAsync(ct);
        if (lockedPropertyId == Guid.Empty)
        {
            return null;
        }

        var existing = await db.Set<PropertyOwnershipTransfer>().AsNoTracking()
            .SingleOrDefaultAsync(transfer => transfer.SourceRef == command.SourceRef, ct);
        if (existing is not null)
        {
            if (existing.PropertyId != command.PropertyId
                || existing.ToOwnerId != command.ToOwnerId
                || existing.EffectiveDate != command.EffectiveDate)
            {
                throw new ValidationException(
                    "The source reference already identifies a different property ownership transfer.");
            }

            return new PropertyOwnershipTransferResult(
                existing.PropertyId,
                existing.FromOwnerId,
                existing.ToOwnerId,
                existing.EffectiveDate,
                DepositEntryId: null);
        }

        var property = await db.Set<Property>().NotSystem()
            .SingleOrDefaultAsync(item => item.Id == command.PropertyId, ct);
        if (property is null)
        {
            return null;
        }

        var ownerExists = await db.Set<Owner>().NotSystem()
            .AnyAsync(owner => owner.Id == command.ToOwnerId, ct);
        if (!ownerExists)
        {
            throw new ValidationException("The succeeding owner does not exist in this organization.");
        }

        if (property.OwnerId == command.ToOwnerId)
        {
            throw new ValidationException("The succeeding owner must differ from the current owner.");
        }

        var latestEffectiveDate = await db.Set<PropertyOwnershipTransfer>()
            .Where(transfer => transfer.PropertyId == property.Id)
            .OrderByDescending(transfer => transfer.EffectiveDate)
            .Select(transfer => (DateOnly?)transfer.EffectiveDate)
            .FirstOrDefaultAsync(ct);
        if (latestEffectiveDate is not null && command.EffectiveDate <= latestEffectiveDate)
        {
            throw new ValidationException(
                "A property ownership transfer must be effective after its preceding transfer.");
        }

        var fromOwnerId = property.OwnerId;
        var depositEntryId = await depositTransfer.TransferAsync(
            property.Id,
            fromOwnerId,
            command.ToOwnerId,
            command.EffectiveDate,
            command.SourceRef,
            ct);

        property.OwnerId = command.ToOwnerId;
        db.Set<PropertyOwnershipTransfer>().Add(new PropertyOwnershipTransfer
        {
            Id = UuidV7.NewId(),
            PropertyId = property.Id,
            FromOwnerId = fromOwnerId,
            ToOwnerId = command.ToOwnerId,
            EffectiveDate = command.EffectiveDate,
            SourceRef = command.SourceRef,
        });
        await db.SaveChangesAsync(ct);

        return new PropertyOwnershipTransferResult(
            property.Id, fromOwnerId, command.ToOwnerId, command.EffectiveDate, depositEntryId);
    }
}
