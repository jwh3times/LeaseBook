using FluentValidation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Tenants;

public sealed record CreateTenant(string DisplayName, string? ContactEmail, string? ContactPhone, string Status)
    : ICommand<Guid>;

public sealed record UpdateTenant(Guid Id, string DisplayName, string? ContactEmail, string? ContactPhone, string Status)
    : ICommand<bool>;

public sealed class CreateTenantValidator : AbstractValidator<CreateTenant>
{
    public CreateTenantValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ContactEmail).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.ContactEmail));
        RuleFor(x => x.Status).Must(v => TenantStatusConverter.DbValues.Contains(v))
            .WithMessage($"Status must be one of: {string.Join(", ", TenantStatusConverter.DbValues)}.");
    }
}

public sealed class UpdateTenantValidator : AbstractValidator<UpdateTenant>
{
    public UpdateTenantValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ContactEmail).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.ContactEmail));
        RuleFor(x => x.Status).Must(v => TenantStatusConverter.DbValues.Contains(v))
            .WithMessage($"Status must be one of: {string.Join(", ", TenantStatusConverter.DbValues)}.");
    }
}

internal sealed class CreateTenantHandler(DbContext db) : ICommandHandler<CreateTenant, Guid>
{
    public async Task<Guid> Handle(CreateTenant command, CancellationToken ct)
    {
        var tenant = new Tenant
        {
            Id = UuidV7.NewId(),
            DisplayName = command.DisplayName,
            ContactEmail = command.ContactEmail,
            ContactPhone = command.ContactPhone,
            Status = TenantStatusConverter.FromDb(command.Status),
        };
        db.Set<Tenant>().Add(tenant);
        await db.SaveChangesAsync(ct);
        return tenant.Id;
    }
}

internal sealed class UpdateTenantHandler(DbContext db) : ICommandHandler<UpdateTenant, bool>
{
    public async Task<bool> Handle(UpdateTenant command, CancellationToken ct)
    {
        var tenant = await db.Set<Tenant>().NotSystem().FirstOrDefaultAsync(t => t.Id == command.Id, ct);
        if (tenant is null)
        {
            return false;
        }

        tenant.DisplayName = command.DisplayName;
        tenant.ContactEmail = command.ContactEmail;
        tenant.ContactPhone = command.ContactPhone;
        tenant.Status = TenantStatusConverter.FromDb(command.Status);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
