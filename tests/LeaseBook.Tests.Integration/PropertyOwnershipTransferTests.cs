using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Reporting;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

[Collection(nameof(DatabaseCollection))]
public sealed class PropertyOwnershipTransferTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";

    [Fact]
    public async Task Staff_can_record_an_ownership_transfer_through_the_directory_api()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);
        Guid propertyId = default;
        Guid buyerId = default;

        await RunAsync(orgId, async (sender, _) =>
        {
            var sellerId = await sender.Send(
                new CreateOwner("Owner Alpha", null, null, null, 800, 0m), ct);
            buyerId = await sender.Send(
                new CreateOwner("Owner Beta", null, null, null, 800, 0m), ct);
            propertyId = await sender.Send(
                new CreateProperty(sellerId, "1 API St", "Raleigh", "NC", null, null), ct);
        }, ct);

        var client = await LoggedInClientAsync(orgId, ct);
        var request = new
        {
            toOwnerId = buyerId,
            effectiveDate = new DateOnly(2026, 2, 2),
            sourceRef = "property-sale:http",
        };
        var response = await client.PostAsJsonAsync(
            $"/api/directory/properties/{propertyId}/ownership-transfers",
            request,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var retry = await client.PostAsJsonAsync(
            $"/api/directory/properties/{propertyId}/ownership-transfers", request, ct);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK, "a retry with the same source reference is idempotent");

        var property = await client.GetFromJsonAsync<PropertyDetail>(
            $"/api/directory/properties/{propertyId}", ct);
        property.ShouldNotBeNull();
        property.OwnerId.ShouldBe(buyerId);
    }

    [Fact]
    public async Task Transfer_is_rejected_when_an_affected_deposit_bank_month_is_reconciled()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, _) =>
        {
            var sellerId = await sender.Send(
                new CreateOwner("Owner Alpha", null, null, null, 800, 0m), ct);
            var buyerId = await sender.Send(
                new CreateOwner("Owner Beta", null, null, null, 800, 0m), ct);
            var propertyId = await sender.Send(
                new CreateProperty(sellerId, "1 Reconciled St", "Raleigh", "NC", null, null), ct);
            var unitId = await sender.Send(
                new CreateUnit(propertyId, "A", 1500m, "available"), ct);
            var tenantId = await sender.Send(
                new CreateTenant("Jasmine Carter", null, null, "current"), ct);
            await sender.Send(new CreateLease(
                tenantId, unitId, new DateOnly(2026, 1, 1), null,
                1500m, 1500m, "active"), ct);
            var depositBank = await sender.Send(
                new CreateBankAccount("Deposit Trust", null, null, "deposit"), ct);

            await sender.Send(new CollectDeposit(
                tenantId, 1500m, new DateOnly(2026, 1, 31), depositBank.Id,
                null, "deposit:before-reconciled-sale"), ct);
            var reconciliation = await sender.Send(
                new StartReconciliation(depositBank.Id, 2026, 2, 0m), ct);
            await sender.Send(new FinalizeReconciliation(reconciliation.Id), ct);

            await Should.ThrowAsync<AccountPeriodLockedException>(() => sender.Send(
                new TransferPropertyOwnership(
                    propertyId, buyerId, new DateOnly(2026, 2, 2), "property-sale:reconciled"),
                ct));

            var property = await sender.Query(new GetPropertyDetail(propertyId), ct);
            property.ShouldNotBeNull();
            property.OwnerId.ShouldBe(sellerId);
        }, ct);
    }

    [Fact]
    public async Task Backdated_disposition_cannot_draw_from_a_bucket_already_moved_to_the_buyer()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, services) =>
        {
            var sellerId = await sender.Send(
                new CreateOwner("Owner Alpha", null, null, null, 800, 0m), ct);
            var buyerId = await sender.Send(
                new CreateOwner("Owner Beta", null, null, null, 800, 0m), ct);
            var propertyId = await sender.Send(
                new CreateProperty(sellerId, "1 Backdate St", "Raleigh", "NC", null, null), ct);
            var unitId = await sender.Send(
                new CreateUnit(propertyId, "A", 1500m, "available"), ct);
            var tenantId = await sender.Send(
                new CreateTenant("Jasmine Carter", null, null, "current"), ct);
            await sender.Send(new CreateLease(
                tenantId, unitId, new DateOnly(2026, 1, 1), null,
                1500m, 1500m, "active"), ct);
            var operating = await sender.Send(
                new CreateBankAccount("Operating Trust", null, null, "trust"), ct);
            var deposits = await sender.Send(
                new CreateBankAccount("Deposit Trust", null, null, "deposit"), ct);

            await sender.Send(new CollectDeposit(
                tenantId, 1500m, new DateOnly(2026, 2, 1), deposits.Id,
                null, "deposit:before-backdated-sale"), ct);
            await sender.Send(new TransferPropertyOwnership(
                propertyId, buyerId, new DateOnly(2026, 2, 2), "property-sale:backdated"), ct);

            await Should.ThrowAsync<InsufficientLiabilityException>(() => sender.Send(new ApplyDeposit(
                tenantId, 100m, new DateOnly(2026, 2, 1), deposits.Id, operating.Id,
                "to-owner-income", "late-entered damages", "deposit:backdated-disposition"), ct));

            var balances = await sender.Query(new GetOwnerBalances(), ct);
            balances.Rows.Single(row => row.OwnerId == sellerId).Deposits.ShouldBe(0m);
            balances.Rows.Single(row => row.OwnerId == buyerId).Deposits.ShouldBe(1500m);
            (await services.GetRequiredService<IInvariantChecks>().CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);
    }

    [Fact]
    public async Task Backdated_transfer_is_rejected_when_later_seller_deposit_activity_would_be_stranded()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, services) =>
        {
            var sellerId = await sender.Send(
                new CreateOwner("Owner Alpha", null, null, null, 800, 0m), ct);
            var buyerId = await sender.Send(
                new CreateOwner("Owner Beta", null, null, null, 800, 0m), ct);
            var propertyId = await sender.Send(
                new CreateProperty(sellerId, "1 Later Deposit St", "Raleigh", "NC", null, null), ct);
            var unitId = await sender.Send(
                new CreateUnit(propertyId, "A", 1500m, "available"), ct);
            var tenantId = await sender.Send(
                new CreateTenant("Jasmine Carter", null, null, "current"), ct);
            await sender.Send(new CreateLease(
                tenantId, unitId, new DateOnly(2026, 1, 1), null,
                1500m, 1500m, "active"), ct);
            var deposits = await sender.Send(
                new CreateBankAccount("Deposit Trust", null, null, "deposit"), ct);

            await sender.Send(new CollectDeposit(
                tenantId, 1500m, new DateOnly(2026, 2, 3), deposits.Id,
                null, "deposit:after-proposed-sale"), ct);

            await Should.ThrowAsync<FluentValidation.ValidationException>(() => sender.Send(
                new TransferPropertyOwnership(
                    propertyId, buyerId, new DateOnly(2026, 2, 2), "property-sale:backdated-cutover"),
                ct));

            var property = await sender.Query(new GetPropertyDetail(propertyId), ct);
            property.ShouldNotBeNull();
            property.OwnerId.ShouldBe(sellerId);
            (await services.GetRequiredService<IInvariantChecks>().CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);
    }

    [Fact]
    public async Task Transfer_is_rejected_when_its_effective_month_is_closed_even_without_deposits()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, services) =>
        {
            var sellerId = await sender.Send(
                new CreateOwner("Owner Alpha", null, null, null, 800, 0m), ct);
            var buyerId = await sender.Send(
                new CreateOwner("Owner Beta", null, null, null, 800, 0m), ct);
            var propertyId = await sender.Send(
                new CreateProperty(sellerId, "1 Locked St", "Raleigh", "NC", null, null), ct);

            var db = services.GetRequiredService<AppDbContext>();
            await new AccountingPeriods(db).CloseAsync(2026, 2, ct);

            await Should.ThrowAsync<PeriodClosedException>(() => sender.Send(
                new TransferPropertyOwnership(
                    propertyId, buyerId, new DateOnly(2026, 2, 2), "property-sale:closed"),
                ct));

            var property = await sender.Query(new GetPropertyDetail(propertyId), ct);
            property.ShouldNotBeNull();
            property.OwnerId.ShouldBe(sellerId);
        }, ct);
    }

    [Fact]
    public async Task Future_dated_transfer_is_rejected_without_changing_the_current_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, _) =>
        {
            var sellerId = await sender.Send(
                new CreateOwner("Owner Alpha", null, null, null, 800, 0m), ct);
            var buyerId = await sender.Send(
                new CreateOwner("Owner Beta", null, null, null, 800, 0m), ct);
            var propertyId = await sender.Send(
                new CreateProperty(sellerId, "1 Future St", "Raleigh", "NC", null, null), ct);

            await Should.ThrowAsync<FluentValidation.ValidationException>(() => sender.Send(
                new TransferPropertyOwnership(
                    propertyId, buyerId, new DateOnly(2099, 1, 1), "property-sale:future"),
                ct));

            var property = await sender.Query(new GetPropertyDetail(propertyId), ct);
            property.ShouldNotBeNull();
            property.OwnerId.ShouldBe(sellerId);
        }, ct);
    }

    [Fact]
    public async Task New_financial_events_use_the_owner_effective_on_their_accounting_date()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, _) =>
        {
            var sellerId = await sender.Send(
                new CreateOwner("Owner Alpha", null, null, null, 800, 0m), ct);
            var buyerId = await sender.Send(
                new CreateOwner("Owner Beta", null, null, null, 800, 0m), ct);
            var propertyId = await sender.Send(
                new CreateProperty(sellerId, "1 History St", "Raleigh", "NC", null, null), ct);
            var unitId = await sender.Send(
                new CreateUnit(propertyId, "A", 1500m, "available"), ct);
            var tenantId = await sender.Send(
                new CreateTenant("Jasmine Carter", null, null, "current"), ct);
            await sender.Send(new CreateLease(
                tenantId, unitId, new DateOnly(2026, 1, 1), null,
                1500m, 1500m, "active"), ct);
            await sender.Send(new CreateBankAccount(
                "Operating Trust", null, null, "trust"), ct);

            await sender.Send(new TransferPropertyOwnership(
                propertyId, buyerId, new DateOnly(2026, 2, 2), "property-sale:dated"), ct);

            await sender.Send(new AddCharge(
                tenantId, 100m, new DateOnly(2026, 2, 1), "rent",
                "Late-entered pre-sale charge", "charge:before-sale"), ct);
            await sender.Send(new AddCharge(
                tenantId, 200m, new DateOnly(2026, 2, 3), "rent",
                "Post-sale charge", "charge:after-sale"), ct);

            var sellerLedger = await sender.Query(new GetOwnerLedger(sellerId, "accrual"), ct);
            var buyerLedger = await sender.Query(new GetOwnerLedger(buyerId, "accrual"), ct);

            sellerLedger.Balance.ShouldBe(100m);
            buyerLedger.Balance.ShouldBe(200m);
        }, ct);
    }

    [Fact]
    public async Task Rent_schedule_uses_the_owner_effective_on_the_charge_date()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, _) =>
        {
            var sellerId = await sender.Send(
                new CreateOwner("Owner Alpha", null, null, null, 800, 0m), ct);
            var buyerId = await sender.Send(
                new CreateOwner("Owner Beta", null, null, null, 800, 0m), ct);
            var propertyId = await sender.Send(
                new CreateProperty(sellerId, "1 Rent Run St", "Raleigh", "NC", null, null), ct);
            var unitId = await sender.Send(
                new CreateUnit(propertyId, "A", 1500m, "available"), ct);
            var tenantId = await sender.Send(
                new CreateTenant("Jasmine Carter", null, null, "current"), ct);
            await sender.Send(new CreateLease(
                tenantId, unitId, new DateOnly(2026, 1, 1), null,
                1500m, 1500m, "active"), ct);

            await sender.Send(new TransferPropertyOwnership(
                propertyId, buyerId, new DateOnly(2026, 2, 2), "property-sale:rent-run"), ct);

            var february = await sender.Query(new GetActiveLeaseSchedule(2026, 2), ct);
            var februaryAssessment = await sender.Query(
                new GetActiveLeaseSchedule(2026, 2, new DateOnly(2026, 2, 20)), ct);
            var march = await sender.Query(new GetActiveLeaseSchedule(2026, 3), ct);

            february.Rows.ShouldHaveSingleItem().OwnerId.ShouldBe(sellerId);
            februaryAssessment.Rows.ShouldHaveSingleItem().OwnerId.ShouldBe(buyerId);
            march.Rows.ShouldHaveSingleItem().OwnerId.ShouldBe(buyerId);
        }, ct);
    }

    [Fact]
    public async Task Ordinary_property_edit_cannot_change_the_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, _) =>
        {
            var ownerId = await sender.Send(
                new CreateOwner("Owner Alpha", null, null, null, 800, 0m), ct);
            var propertyId = await sender.Send(
                new CreateProperty(ownerId, "1 Old St", "Raleigh", "NC", null, null), ct);

            await sender.Send(new UpdateProperty(
                propertyId, "2 New St", "Durham", "NC", "27701", 900), ct);

            var property = await sender.Query(new GetPropertyDetail(propertyId), ct);
            property.ShouldNotBeNull();
            property.OwnerId.ShouldBe(ownerId);
            property.Address.ShouldBe("2 New St");
        }, ct);
    }

    [Fact]
    public async Task Sale_moves_the_held_deposit_to_the_buyer_before_disposition()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        await RunAsync(orgId, async (sender, services) =>
        {
            var sellerId = await sender.Send(
                new CreateOwner("Owner Alpha", null, null, null, 800, 0m), ct);
            var buyerId = await sender.Send(
                new CreateOwner("Owner Beta", null, null, null, 800, 0m), ct);
            var propertyId = await sender.Send(
                new CreateProperty(sellerId, "1 Sale St", "Raleigh", "NC", null, null), ct);
            var unitId = await sender.Send(
                new CreateUnit(propertyId, "A", 1500m, "available"), ct);
            var tenantId = await sender.Send(
                new CreateTenant("Jasmine Carter", null, null, "current"), ct);
            await sender.Send(new CreateLease(
                tenantId, unitId, new DateOnly(2026, 1, 1), null,
                1500m, 1500m, "active"), ct);
            var operating = await sender.Send(
                new CreateBankAccount("Operating Trust", null, null, "trust"), ct);
            var deposits = await sender.Send(
                new CreateBankAccount("Deposit Trust", null, null, "deposit"), ct);

            await sender.Send(new CollectDeposit(
                tenantId, 1500m, new DateOnly(2026, 2, 1), deposits.Id,
                null, "deposit:collected-under-alpha"), ct);

            await sender.Send(new TransferPropertyOwnership(
                propertyId, buyerId, new DateOnly(2026, 2, 2), "property-sale:alpha-to-beta"), ct);

            var afterSale = await sender.Query(new GetOwnerBalances(), ct);
            afterSale.Rows.Single(row => row.OwnerId == sellerId).Deposits.ShouldBe(0m);
            afterSale.Rows.Single(row => row.OwnerId == buyerId).Deposits.ShouldBe(1500m);

            await sender.Send(new ApplyDeposit(
                tenantId, 1500m, new DateOnly(2026, 2, 3), deposits.Id, operating.Id,
                "to-owner-income", "move-out damages", "deposit:disposed-under-beta"), ct);

            var afterDisposition = await sender.Query(new GetOwnerBalances(), ct);
            afterDisposition.Rows.Single(row => row.OwnerId == sellerId).Total.ShouldBe(0m);
            afterDisposition.Rows.Single(row => row.OwnerId == buyerId).Deposits.ShouldBe(0m);
            afterDisposition.Rows.Single(row => row.OwnerId == buyerId).Operating.ShouldBe(1500m);

            var equation = await sender.Query(new GetTrustEquation(), ct);
            equation.Rows.ShouldAllBe(row => row.Variance == 0m);

            var violations = await services.GetRequiredService<IInvariantChecks>().CheckCoreAsync(ct);
            violations.ShouldBeEmpty();
        }, ct);
    }

    private async Task<Guid> NewOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Property Sale Org {orgId:N}" });
        await migratorDb.SaveChangesAsync(ct);
        return orgId;
    }

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

    private async Task<HttpClient> LoggedInClientAsync(Guid orgId, CancellationToken ct)
    {
        var email = $"ownership-{orgId:N}@example.com";
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser
            {
                Id = UuidV7.NewId(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                OrgId = orgId,
                DisplayName = "Ownership Clerk",
            };
            (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
        }

        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.PrimeCsrfAsync(ct);
        return client;
    }
}
