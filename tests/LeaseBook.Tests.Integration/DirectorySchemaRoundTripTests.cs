using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Shouldly;

// Testcontainers pulls in BouncyCastle, whose root namespace `Org` shadows the entity type.
using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-01 migration smoke test: proves the seven directory tables exist with their guarantees by writing
/// one of each entity through the RLS-subject <b>app role</b> inside an org scope, reading them back, and
/// checking the NUMERIC(14,2) Money round-trip is exact and the enum/text mappings survive. The
/// schema-guard test (run alongside in this suite) separately proves all seven are FORCE-RLS with a
/// policy. The journal-dimension FKs are <i>not</i> exercised here (no journal rows in this scope) —
/// WP-06's golden seed is the FK proof.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class DirectorySchemaRoundTripTests(PostgresFixture fixture)
{
    [Fact]
    public async Task One_of_each_directory_entity_round_trips_through_the_app_role()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        await CreateOrgAsync(orgId, ct);

        var tenant = new TenantContext();
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        var ownerId = UuidV7.NewId();
        var propertyId = UuidV7.NewId();
        var unitId = UuidV7.NewId();
        var tenantRowId = UuidV7.NewId();
        var leaseId = UuidV7.NewId();
        var bankId = UuidV7.NewId();
        var settingsId = UuidV7.NewId();

        await executor.RunAsync(orgId, async () =>
        {
            // Insert in FK dependency order: owner → property → unit; tenant; lease (tenant+unit); etc.
            db.Set<Owner>().Add(new Owner
            {
                Id = ownerId,
                Name = "Hargrove Family Trust",
                Initials = "HF",
                ContactEmail = "trust@hargrove.example",
                DefaultMgmtFeeBps = 800,
                ReserveAmount = new Money(500.00m),
            });
            db.Set<Tenant>().Add(new Tenant
            {
                Id = tenantRowId,
                DisplayName = "Jasmine Carter",
                ContactPhone = "919-555-0142",
                Status = TenantStatus.Current,
            });
            db.Set<Property>().Add(new Property
            {
                Id = propertyId,
                OwnerId = ownerId,
                Address = "118 Pryor St",
                City = "Durham",
                State = "NC",
                Zip = "27701",
                MgmtFeeBps = 750,
            });
            db.Set<Unit>().Add(new Unit
            {
                Id = unitId,
                PropertyId = propertyId,
                Label = "#2B",
                Rent = new Money(1450.00m),
                Status = UnitStatus.Occupied,
            });
            db.Set<LeaseLite>().Add(new LeaseLite
            {
                Id = leaseId,
                TenantId = tenantRowId,
                UnitId = unitId,
                StartDate = new DateOnly(2025, 6, 1),
                EndDate = new DateOnly(2026, 5, 31),
                Rent = new Money(1450.00m),
                DepositRequired = new Money(1450.00m),
                Status = LeaseStatus.Active,
            });
            db.Set<BankAccount>().Add(new BankAccount
            {
                Id = bankId,
                Name = "Operating Trust",
                Institution = "First Citizens",
                Mask = "4021",
                Purpose = BankPurpose.Trust,
            });
            db.Set<OrgSettings>().Add(new OrgSettings
            {
                Id = settingsId,
                AccountingBasis = AccountingBasis.Cash,
                MoneyNegativeDisplay = MoneyNegativeDisplay.Minus,
                LegalName = "Tarheel Property Group",
            });
            await db.SaveChangesAsync(ct);
        }, ct);

        await executor.RunAsync(orgId, async () =>
        {
            var owner = await db.Set<Owner>().AsNoTracking().SingleAsync(o => o.Id == ownerId, ct);
            owner.OrgId.ShouldBe(orgId);                       // stamped by the interceptor
            owner.Name.ShouldBe("Hargrove Family Trust");
            owner.DefaultMgmtFeeBps.ShouldBe(800);
            owner.ReserveAmount.Amount.ShouldBe(500.00m);       // exact NUMERIC(14,2) round-trip
            owner.IsSystem.ShouldBeFalse();                    // store default
            owner.CreatedAt.ShouldNotBe(default);              // stamped on insert

            var property = await db.Set<Property>().AsNoTracking().SingleAsync(p => p.Id == propertyId, ct);
            property.OwnerId.ShouldBe(ownerId);
            property.City.ShouldBe("Durham");
            property.MgmtFeeBps.ShouldBe(750);

            var unit = await db.Set<Unit>().AsNoTracking().SingleAsync(u => u.Id == unitId, ct);
            unit.PropertyId.ShouldBe(propertyId);
            unit.Rent.Amount.ShouldBe(1450.00m);
            unit.Status.ShouldBe(UnitStatus.Occupied);

            var tenantRow = await db.Set<Tenant>().AsNoTracking().SingleAsync(t => t.Id == tenantRowId, ct);
            tenantRow.DisplayName.ShouldBe("Jasmine Carter");
            tenantRow.Status.ShouldBe(TenantStatus.Current);

            var lease = await db.Set<LeaseLite>().AsNoTracking().SingleAsync(l => l.Id == leaseId, ct);
            lease.TenantId.ShouldBe(tenantRowId);
            lease.UnitId.ShouldBe(unitId);
            lease.StartDate.ShouldBe(new DateOnly(2025, 6, 1));
            lease.DepositRequired.Amount.ShouldBe(1450.00m);
            lease.Status.ShouldBe(LeaseStatus.Active);

            var bank = await db.Set<BankAccount>().AsNoTracking().SingleAsync(b => b.Id == bankId, ct);
            bank.Purpose.ShouldBe(BankPurpose.Trust);
            bank.IsActive.ShouldBeTrue();                      // store default true
            bank.Mask.ShouldBe("4021");

            var settings = await db.Set<OrgSettings>().AsNoTracking().SingleAsync(s => s.Id == settingsId, ct);
            settings.AccountingBasis.ShouldBe(AccountingBasis.Cash);
            settings.MoneyNegativeDisplay.ShouldBe(MoneyNegativeDisplay.Minus);
            settings.LegalName.ShouldBe("Tarheel Property Group");
        }, ct);
    }

    [Fact]
    public async Task Org_settings_is_unique_per_org()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        await CreateOrgAsync(orgId, ct);

        var tenant = new TenantContext();
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        await executor.RunAsync(orgId, async () =>
        {
            db.Set<OrgSettings>().Add(new OrgSettings { Id = UuidV7.NewId() });
            await db.SaveChangesAsync(ct);
        }, ct);

        // A second settings row for the same org violates the unique (org_id) index (§C.1, P46).
        var ex = await Should.ThrowAsync<DbUpdateException>(() => executor.RunAsync(orgId, async () =>
        {
            db.Set<OrgSettings>().Add(new OrgSettings { Id = UuidV7.NewId() });
            await db.SaveChangesAsync(ct);
        }, ct));
        ex.ShouldNotBeNull();
    }

    private async Task CreateOrgAsync(Guid orgId, CancellationToken ct)
    {
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = "Tarheel Property Group" });
        await migratorDb.SaveChangesAsync(ct);
    }
}
