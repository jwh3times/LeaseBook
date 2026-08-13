using FluentValidation;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

[Collection(nameof(DatabaseCollection))]
public sealed class FinancialAttributionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Database_rejects_two_active_leases_for_one_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var tenantId = Guid.Empty;
        var firstUnit = Guid.Empty;
        var secondUnit = Guid.Empty;

        await RunAsync(orgId, async (sender, _) =>
        {
            var owner = await sender.Send(
                new CreateOwner("Owner Alpha", null, null, null, 800, 0m), ct);
            var property = await sender.Send(
                new CreateProperty(owner, "1 Alpha Ave", "Raleigh", "NC", null, null), ct);
            firstUnit = await sender.Send(
                new CreateUnit(property, "A", 500m, "occupied"), ct);
            secondUnit = await sender.Send(
                new CreateUnit(property, "B", 700m, "occupied"), ct);
            tenantId = await sender.Send(
                new CreateTenant("Jasmine Carter", null, null, "current"), ct);
        }, ct);

        await Should.ThrowAsync<DbUpdateException>(() => RunAsync(orgId, async (_, services) =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            db.Set<LeaseLite>().AddRange(
                ActiveLease(tenantId, firstUnit, 500m),
                ActiveLease(tenantId, secondUnit, 700m));

            await db.SaveChangesAsync(ct);
        }, ct));
    }

    [Fact]
    public async Task Creating_a_second_active_lease_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, _) =>
        {
            var ownerAlpha = await sender.Send(
                new CreateOwner("Owner Alpha", null, null, null, 800, 0m), ct);
            var ownerBeta = await sender.Send(
                new CreateOwner("Owner Beta", null, null, null, 800, 0m), ct);
            var propertyAlpha = await sender.Send(
                new CreateProperty(ownerAlpha, "1 Alpha Ave", "Raleigh", "NC", null, null), ct);
            var propertyBeta = await sender.Send(
                new CreateProperty(ownerBeta, "2 Beta Ave", "Raleigh", "NC", null, null), ct);
            var unitAlpha = await sender.Send(
                new CreateUnit(propertyAlpha, "A", 500m, "occupied"), ct);
            var unitBeta = await sender.Send(
                new CreateUnit(propertyBeta, "B", 700m, "occupied"), ct);
            var tenantId = await sender.Send(
                new CreateTenant("Jasmine Carter", null, null, "current"), ct);
            await sender.Send(new CreateLease(
                tenantId, unitAlpha, new DateOnly(2026, 1, 1), null,
                500m, 500m, "active"), ct);

            var exception = await Should.ThrowAsync<ValidationException>(() => sender.Send(
                new CreateLease(
                    tenantId, unitBeta, new DateOnly(2026, 1, 1), null,
                    700m, 700m, "active"),
                ct));

            exception.Errors.ShouldContain(error =>
                error.PropertyName == "tenantId"
                && error.ErrorMessage.Contains("one active lease", StringComparison.OrdinalIgnoreCase));
        }, ct);
    }

    [Fact]
    public async Task Second_active_lease_is_rejected_and_partial_payment_stays_with_the_active_lease_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, services) =>
        {
            var ownerAlpha = await sender.Send(
                new CreateOwner("Owner Alpha", null, null, null, 800, 0m), ct);
            var ownerBeta = await sender.Send(
                new CreateOwner("Owner Beta", null, null, null, 800, 0m), ct);
            var propertyAlpha = await sender.Send(
                new CreateProperty(ownerAlpha, "1 Alpha Ave", "Raleigh", "NC", null, null), ct);
            var propertyBeta = await sender.Send(
                new CreateProperty(ownerBeta, "2 Beta Ave", "Raleigh", "NC", null, null), ct);
            var unitAlpha = await sender.Send(
                new CreateUnit(propertyAlpha, "A", 500m, "occupied"), ct);
            var unitBeta = await sender.Send(
                new CreateUnit(propertyBeta, "B", 700m, "occupied"), ct);
            var tenantId = await sender.Send(
                new CreateTenant("Jasmine Carter", null, null, "current"), ct);
            await sender.Send(new CreateLease(
                tenantId, unitAlpha, new DateOnly(2026, 1, 1), null,
                500m, 500m, "active"), ct);
            var pendingLease = await sender.Send(new CreateLease(
                tenantId, unitBeta, new DateOnly(2026, 1, 1), null,
                700m, 700m, "pending"), ct);

            var activation = new UpdateLease(
                pendingLease, tenantId, unitBeta, new DateOnly(2026, 1, 1), null,
                700m, 700m, "active");

            var exception = await Should.ThrowAsync<ValidationException>(
                () => sender.Send(activation, ct));
            exception.Errors.ShouldContain(error =>
                error.PropertyName == "tenantId"
                && error.ErrorMessage.Contains("one active lease", StringComparison.OrdinalIgnoreCase));

            var trustBank = await sender.Send(
                new CreateBankAccount("Operating Trust", null, null, "trust"), ct);
            await sender.Send(new AddCharge(
                tenantId, 500m, new DateOnly(2026, 2, 1), "rent", null, "rent:alpha"), ct);
            var payment = await sender.Send(new RecordPayment(
                tenantId, 300m, new DateOnly(2026, 2, 5), "ach", trustBank.Id,
                null, "payment:partial"), ct);

            var db = services.GetRequiredService<AppDbContext>();
            var cashOwnerLines = await db.Set<JournalLine>()
                .Where(line =>
                    line.EntryId == payment.EntryId
                    && line.AccountClass == AccountClass.OwnerEquity
                    && line.Basis == EntryBasis.Cash)
                .Select(line => new { line.OwnerId, Credit = line.Credit!.Value.Amount })
                .ToListAsync(ct);

            cashOwnerLines.Count.ShouldBe(1);
            var line = cashOwnerLines[0];
            line.OwnerId.ShouldBe(ownerAlpha);
            line.Credit.ShouldBe(300m);
            cashOwnerLines.ShouldNotContain(ownerLine => ownerLine.OwnerId == ownerBeta);
        }, ct);
    }

    private async Task<Guid> NewOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Attribution Org {orgId:N}" });
        await migratorDb.SaveChangesAsync(ct);
        return orgId;
    }

    private static LeaseLite ActiveLease(Guid tenantId, Guid unitId, decimal rent) => new()
    {
        Id = UuidV7.NewId(),
        TenantId = tenantId,
        UnitId = unitId,
        StartDate = new DateOnly(2026, 1, 1),
        Rent = new Money(rent),
        DepositRequired = new Money(rent),
        Status = LeaseStatus.Active,
    };

    private async Task RunAsync(
        Guid orgId,
        Func<ISender, IServiceProvider, Task> work,
        CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        await executor.RunAsSystemAsync(
            orgId,
            "test-harness",
            () => work(scope.ServiceProvider.GetRequiredService<ISender>(), scope.ServiceProvider),
            ct);
    }
}
