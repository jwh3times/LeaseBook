using FluentValidation;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.Modules.Directory.Persistence;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Directory.Features.Leases;

public sealed record CreateLease(
    Guid TenantId, Guid UnitId, DateOnly? StartDate, DateOnly? EndDate,
    decimal Rent, decimal DepositRequired, string Status,
    // Late-fee per-lease overrides (WP-3 / NC §42-46). All optional; null = use org default.
    int? LateFeeRentDueDayOverride = null,
    int? LateFeeGraceDaysOverride = null,
    string? LateFeeKindOverride = null,
    decimal? LateFeeAmountOverride = null,
    int? LateFeeRateBpsOverride = null) : ICommand<Guid>;

public sealed record UpdateLease(
    Guid Id, Guid TenantId, Guid UnitId, DateOnly? StartDate, DateOnly? EndDate,
    decimal Rent, decimal DepositRequired, string Status,
    // Late-fee per-lease overrides (WP-3 / NC §42-46). All optional; null = use org default.
    int? LateFeeRentDueDayOverride = null,
    int? LateFeeGraceDaysOverride = null,
    string? LateFeeKindOverride = null,
    decimal? LateFeeAmountOverride = null,
    int? LateFeeRateBpsOverride = null) : ICommand<bool>;

public sealed class CreateLeaseValidator : AbstractValidator<CreateLease>
{
    public CreateLeaseValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.UnitId).NotEmpty();
        RuleFor(x => x.Rent).MoneyAmount();
        RuleFor(x => x.DepositRequired).MoneyAmount();
        RuleFor(x => x.Status).Must(v => LeaseStatusConverter.DbValues.Contains(v))
            .WithMessage($"status must be one of: {string.Join(", ", LeaseStatusConverter.DbValues)}.");
        RuleFor(x => x.EndDate).GreaterThanOrEqualTo(x => x.StartDate)
            .When(x => x.StartDate.HasValue && x.EndDate.HasValue)
            .WithMessage("endDate must be on or after startDate.");
    }
}

public sealed class UpdateLeaseValidator : AbstractValidator<UpdateLease>
{
    public UpdateLeaseValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.UnitId).NotEmpty();
        RuleFor(x => x.Rent).MoneyAmount();
        RuleFor(x => x.DepositRequired).MoneyAmount();
        RuleFor(x => x.Status).Must(v => LeaseStatusConverter.DbValues.Contains(v))
            .WithMessage($"status must be one of: {string.Join(", ", LeaseStatusConverter.DbValues)}.");
        RuleFor(x => x.EndDate).GreaterThanOrEqualTo(x => x.StartDate)
            .When(x => x.StartDate.HasValue && x.EndDate.HasValue)
            .WithMessage("endDate must be on or after startDate.");
    }
}

internal sealed class CreateLeaseHandler(DbContext db) : ICommandHandler<CreateLease, Guid>
{
    public async Task<Guid> Handle(CreateLease command, CancellationToken ct)
    {
        var lease = new LeaseLite
        {
            Id = UuidV7.NewId(),
            TenantId = command.TenantId,
            UnitId = command.UnitId,
            StartDate = command.StartDate,
            EndDate = command.EndDate,
            Rent = new Money(command.Rent),
            DepositRequired = new Money(command.DepositRequired),
            Status = LeaseStatusConverter.FromDb(command.Status),
            LateFeeRentDueDayOverride = command.LateFeeRentDueDayOverride,
            LateFeeGraceDaysOverride = command.LateFeeGraceDaysOverride,
            LateFeeKindOverride = command.LateFeeKindOverride is null
                ? null
                : LateFeeKindConverter.FromDb(command.LateFeeKindOverride),
            LateFeeAmountOverride = command.LateFeeAmountOverride,
            LateFeeRateBpsOverride = command.LateFeeRateBpsOverride,
        };
        db.Set<LeaseLite>().Add(lease);
        await db.SaveChangesAsync(ct);
        return lease.Id;
    }
}

internal sealed class UpdateLeaseHandler(DbContext db) : ICommandHandler<UpdateLease, bool>
{
    public async Task<bool> Handle(UpdateLease command, CancellationToken ct)
    {
        var lease = await db.Set<LeaseLite>().FirstOrDefaultAsync(l => l.Id == command.Id, ct);
        if (lease is null)
        {
            return false;
        }

        lease.TenantId = command.TenantId;
        lease.UnitId = command.UnitId;
        lease.StartDate = command.StartDate;
        lease.EndDate = command.EndDate;
        lease.Rent = new Money(command.Rent);
        lease.DepositRequired = new Money(command.DepositRequired);
        lease.Status = LeaseStatusConverter.FromDb(command.Status);
        lease.LateFeeRentDueDayOverride = command.LateFeeRentDueDayOverride;
        lease.LateFeeGraceDaysOverride = command.LateFeeGraceDaysOverride;
        lease.LateFeeKindOverride = command.LateFeeKindOverride is null
            ? null
            : LateFeeKindConverter.FromDb(command.LateFeeKindOverride);
        lease.LateFeeAmountOverride = command.LateFeeAmountOverride;
        lease.LateFeeRateBpsOverride = command.LateFeeRateBpsOverride;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
