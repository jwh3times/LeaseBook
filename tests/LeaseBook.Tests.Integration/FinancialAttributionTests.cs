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
using Npgsql;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

[Collection(nameof(DatabaseCollection))]
public sealed class FinancialAttributionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Backdated_payment_uses_the_lease_effective_on_the_payment_date_after_it_has_ended()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, _) =>
        {
            var ownerId = await sender.Send(
                new CreateOwner("Historical Owner", null, null, null, 800, 0m), ct);
            var propertyId = await sender.Send(
                new CreateProperty(ownerId, "1 History Way", "Raleigh", "NC", null, null), ct);
            var unitId = await sender.Send(
                new CreateUnit(propertyId, "A", 500m, "available"), ct);
            var tenantId = await sender.Send(
                new CreateTenant("Former Tenant", null, null, "past"), ct);
            await sender.Send(new CreateLease(
                tenantId,
                unitId,
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 6, 30),
                500m,
                500m,
                "ended"), ct);
            var trustBank = await sender.Send(
                new CreateBankAccount("Operating Trust", null, null, "trust"), ct);

            await sender.Send(new AddCharge(
                tenantId,
                500m,
                new DateOnly(2026, 5, 1),
                "rent",
                null,
                "historical-rent"), ct);
            await sender.Send(new RecordPayment(
                tenantId,
                500m,
                new DateOnly(2026, 5, 5),
                "ach",
                trustBank.Id,
                null,
                "historical-payment"), ct);

            var balances = await sender.Query(new LeaseBook.Modules.Accounting.Features.Ledgers.GetOwnerBalances(), ct);
            var owner = balances.Rows.Single(row => row.OwnerId == ownerId);
            owner.Operating.ShouldBe(500m);
        }, ct);
    }

    [Fact]
    public async Task Posting_after_the_lease_end_is_rejected_even_when_its_lifecycle_status_is_active()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, _) =>
        {
            var ownerId = await sender.Send(
                new CreateOwner("Former Owner", null, null, null, 800, 0m), ct);
            var propertyId = await sender.Send(
                new CreateProperty(ownerId, "2 History Way", "Raleigh", "NC", null, null), ct);
            var unitId = await sender.Send(
                new CreateUnit(propertyId, "B", 700m, "available"), ct);
            var tenantId = await sender.Send(
                new CreateTenant("Expired Tenant", null, null, "past"), ct);
            await sender.Send(new CreateLease(
                tenantId,
                unitId,
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 6, 30),
                700m,
                700m,
                "active"), ct);
            await sender.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct);

            await Should.ThrowAsync<ValidationException>(() => sender.Send(new AddCharge(
                tenantId,
                700m,
                new DateOnly(2026, 7, 1),
                "rent",
                null,
                "expired-rent"), ct));
        }, ct);
    }

    [Fact]
    public async Task Posting_before_the_lease_start_is_rejected_even_when_its_lifecycle_status_is_active()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, _) =>
        {
            var ownerId = await sender.Send(
                new CreateOwner("Future Owner", null, null, null, 800, 0m), ct);
            var propertyId = await sender.Send(
                new CreateProperty(ownerId, "3 History Way", "Raleigh", "NC", null, null), ct);
            var unitId = await sender.Send(
                new CreateUnit(propertyId, "C", 900m, "available"), ct);
            var tenantId = await sender.Send(
                new CreateTenant("Future Tenant", null, null, "current"), ct);
            await sender.Send(new CreateLease(
                tenantId,
                unitId,
                new DateOnly(2026, 8, 1),
                new DateOnly(2027, 7, 31),
                900m,
                900m,
                "active"), ct);
            await sender.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct);

            await Should.ThrowAsync<ValidationException>(() => sender.Send(new AddCharge(
                tenantId,
                900m,
                new DateOnly(2026, 7, 31),
                "rent",
                null,
                "future-rent"), ct));
        }, ct);
    }

    [Fact]
    public async Task Database_rejects_overlapping_non_pending_terms_for_one_tenant()
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
                new CreateUnit(property, "A", 500m, "available"), ct);
            secondUnit = await sender.Send(
                new CreateUnit(property, "B", 700m, "available"), ct);
            tenantId = await sender.Send(
                new CreateTenant("Jasmine Carter", null, null, "current"), ct);
        }, ct);

        var exception = await Should.ThrowAsync<DbUpdateException>(() => RunAsync(orgId, async (_, services) =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            db.Set<LeaseLite>().AddRange(
                ActiveLease(tenantId, firstUnit, 500m),
                ActiveLease(tenantId, secondUnit, 700m));

            await db.SaveChangesAsync(ct);
        }, ct));
        exception.InnerException.ShouldBeOfType<PostgresException>().ConstraintName
            .ShouldBe("ex_lease_lite_tenant_term_non_overlap");
    }

    [Fact]
    public async Task Database_rejects_overlapping_non_pending_terms_for_one_unit()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        var firstTenant = Guid.Empty;
        var secondTenant = Guid.Empty;
        var unitId = Guid.Empty;

        await RunAsync(orgId, async (sender, _) =>
        {
            var owner = await sender.Send(
                new CreateOwner("Owner Unit", null, null, null, 800, 0m), ct);
            var property = await sender.Send(
                new CreateProperty(owner, "1 Unit Ave", "Raleigh", "NC", null, null), ct);
            unitId = await sender.Send(
                new CreateUnit(property, "A", 500m, "available"), ct);
            firstTenant = await sender.Send(
                new CreateTenant("First Resident", null, null, "past"), ct);
            secondTenant = await sender.Send(
                new CreateTenant("Second Resident", null, null, "current"), ct);
        }, ct);

        var exception = await Should.ThrowAsync<DbUpdateException>(() => RunAsync(orgId, async (_, services) =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            db.Set<LeaseLite>().AddRange(
                ActiveLease(firstTenant, unitId, 500m),
                ActiveLease(secondTenant, unitId, 500m));

            await db.SaveChangesAsync(ct);
        }, ct));
        exception.InnerException.ShouldBeOfType<PostgresException>().ConstraintName
            .ShouldBe("ex_lease_lite_unit_term_non_overlap");
    }

    [Fact]
    public async Task Creating_a_second_overlapping_active_lease_is_rejected()
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
                new CreateUnit(propertyAlpha, "A", 500m, "available"), ct);
            var unitBeta = await sender.Send(
                new CreateUnit(propertyBeta, "B", 700m, "available"), ct);
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
                && error.ErrorMessage.Contains("overlap", StringComparison.OrdinalIgnoreCase));
        }, ct);
    }

    [Fact]
    public async Task Creating_a_non_pending_lease_with_an_overlapping_tenant_term_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, _) =>
        {
            var owner = await sender.Send(
                new CreateOwner("Overlap Owner", null, null, null, 800, 0m), ct);
            var property = await sender.Send(
                new CreateProperty(owner, "4 History Way", "Raleigh", "NC", null, null), ct);
            var firstUnit = await sender.Send(
                new CreateUnit(property, "A", 500m, "available"), ct);
            var secondUnit = await sender.Send(
                new CreateUnit(property, "B", 700m, "available"), ct);
            var tenantId = await sender.Send(
                new CreateTenant("Returning Resident", null, null, "current"), ct);
            await sender.Send(new CreateLease(
                tenantId, firstUnit, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30),
                500m, 500m, "ended"), ct);

            var exception = await Should.ThrowAsync<ValidationException>(() => sender.Send(
                new CreateLease(
                    tenantId, secondUnit, new DateOnly(2026, 6, 30), new DateOnly(2027, 5, 31),
                    700m, 700m, "active"),
                ct));

            exception.Errors.ShouldContain(error =>
                error.PropertyName == "tenantId"
                && error.ErrorMessage.Contains("overlap", StringComparison.OrdinalIgnoreCase));
        }, ct);
    }

    [Fact]
    public async Task Creating_a_non_pending_lease_with_an_overlapping_unit_term_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, _) =>
        {
            var owner = await sender.Send(
                new CreateOwner("Unit Owner", null, null, null, 800, 0m), ct);
            var property = await sender.Send(
                new CreateProperty(owner, "5 History Way", "Raleigh", "NC", null, null), ct);
            var unitId = await sender.Send(
                new CreateUnit(property, "A", 500m, "available"), ct);
            var formerTenant = await sender.Send(
                new CreateTenant("Former Resident", null, null, "past"), ct);
            var nextTenant = await sender.Send(
                new CreateTenant("Next Resident", null, null, "current"), ct);
            await sender.Send(new CreateLease(
                formerTenant, unitId, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30),
                500m, 500m, "ended"), ct);

            var exception = await Should.ThrowAsync<ValidationException>(() => sender.Send(
                new CreateLease(
                    nextTenant, unitId, new DateOnly(2026, 6, 30), new DateOnly(2027, 5, 31),
                    500m, 500m, "active"),
                ct));

            exception.Errors.ShouldContain(error =>
                error.PropertyName == "unitId"
                && error.ErrorMessage.Contains("overlap", StringComparison.OrdinalIgnoreCase));
        }, ct);
    }

    [Fact]
    public async Task Sequential_active_lifecycle_rows_are_allowed_when_their_terms_do_not_overlap()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, _) =>
        {
            var owner = await sender.Send(
                new CreateOwner("Sequential Owner", null, null, null, 800, 0m), ct);
            var property = await sender.Send(
                new CreateProperty(owner, "6 History Way", "Raleigh", "NC", null, null), ct);
            var firstUnit = await sender.Send(
                new CreateUnit(property, "A", 500m, "available"), ct);
            var secondUnit = await sender.Send(
                new CreateUnit(property, "B", 700m, "available"), ct);
            var tenantId = await sender.Send(
                new CreateTenant("Renewing Resident", null, null, "current"), ct);

            var firstLease = await sender.Send(new CreateLease(
                tenantId, firstUnit, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30),
                500m, 500m, "active"), ct);
            var secondLease = await sender.Send(new CreateLease(
                tenantId, secondUnit, new DateOnly(2026, 7, 1), new DateOnly(2027, 6, 30),
                700m, 700m, "active"), ct);

            firstLease.ShouldNotBe(Guid.Empty);
            secondLease.ShouldNotBe(Guid.Empty);
        }, ct);
    }

    [Fact]
    public async Task Pending_lease_terms_may_overlap_until_activation()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, _) =>
        {
            var owner = await sender.Send(
                new CreateOwner("Pending Owner", null, null, null, 800, 0m), ct);
            var property = await sender.Send(
                new CreateProperty(owner, "7 History Way", "Raleigh", "NC", null, null), ct);
            var firstUnit = await sender.Send(
                new CreateUnit(property, "A", 500m, "available"), ct);
            var secondUnit = await sender.Send(
                new CreateUnit(property, "B", 700m, "available"), ct);
            var tenantId = await sender.Send(
                new CreateTenant("Pending Resident", null, null, "current"), ct);
            await sender.Send(new CreateLease(
                tenantId, firstUnit, new DateOnly(2026, 1, 1), null,
                500m, 500m, "active"), ct);

            var pendingLease = await sender.Send(new CreateLease(
                tenantId, secondUnit, new DateOnly(2026, 1, 1), null,
                700m, 700m, "pending"), ct);

            pendingLease.ShouldNotBe(Guid.Empty);
        }, ct);
    }

    [Fact]
    public async Task Overlapping_pending_lease_cannot_be_activated_and_payment_stays_with_the_effective_lease_owner()
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
                new CreateUnit(propertyAlpha, "A", 500m, "available"), ct);
            var unitBeta = await sender.Send(
                new CreateUnit(propertyBeta, "B", 700m, "available"), ct);
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
                && error.ErrorMessage.Contains("overlap", StringComparison.OrdinalIgnoreCase));

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
