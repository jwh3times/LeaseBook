using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Settings;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

// Testcontainers pulls in BouncyCastle, whose root namespace `Org` shadows the entity type.
using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-02: org settings (lazy get-or-create + round-trip), the bank-account → chart-of-accounts
/// provisioning seam (ADR-007 port + host adapter), management-fee resolution, and cross-org isolation.
/// Drives the real CQRS pipeline (<see cref="ISender"/>) through the host's DI inside an org transaction,
/// so validators, decorators and the cross-module adapter all run as in a real request.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SettingsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Org_settings_are_lazily_created_and_round_trip_through_an_update()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        // First read materializes the row with defaults.
        var created = await DispatchAsync(orgId, (s, c) => s.Query(new GetOrgSettings(), c), ct);
        created.AccountingBasis.ShouldBe("cash");
        created.MoneyNegativeDisplay.ShouldBe("minus");

        await DispatchAsync(orgId, (s, c) => s.Send(new UpdateOrgSettings(
            "accrual", "parens", "Tarheel Property Group", "1 Pack Sq", "Asheville", "NC", "28801", "828-555-0100", null), c), ct);

        var read = await DispatchAsync(orgId, (s, c) => s.Query(new GetOrgSettings(), c), ct);
        read.AccountingBasis.ShouldBe("accrual");
        read.MoneyNegativeDisplay.ShouldBe("parens");
        read.LegalName.ShouldBe("Tarheel Property Group");
        read.City.ShouldBe("Asheville");
    }

    [Fact]
    public async Task Creating_a_bank_account_provisions_the_accounting_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        var bank = await DispatchAsync(orgId, (s, c) =>
            s.Send(new CreateBankAccount("Operating Trust", "First Citizens", "4021", "trust"), c), ct);
        bank.Purpose.ShouldBe("trust");
        bank.IsActive.ShouldBeTrue();

        // The provisioning seam created the matching accounting account → it now appears (book 0).
        var balances = await DispatchAsync(orgId, (s, c) => s.Query(new GetBankBalances(), c), ct);
        balances.Rows.ShouldContain(r => r.BankAccountId == bank.Id && r.Book == 0m);

        // And the directory list returns it.
        var banks = await DispatchAsync(orgId, (s, c) => s.Query(new ListBankAccounts(), c), ct);
        banks.ShouldContain(b => b.Id == bank.Id && b.Name == "Operating Trust");
    }

    [Fact]
    public async Task Settings_and_banks_are_isolated_across_orgs()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = await NewOrgAsync(ct);
        var orgB = await NewOrgAsync(ct);

        await DispatchAsync(orgA, (s, c) => s.Send(new CreateBankAccount("A Trust", null, null, "trust"), c), ct);
        await DispatchAsync(orgA, (s, c) => s.Send(new UpdateOrgSettings(
            "accrual", "minus", null, null, null, null, null, null, null), c), ct);

        // Org B sees none of A's banks and gets its own default settings.
        var bBanks = await DispatchAsync(orgB, (s, c) => s.Query(new ListBankAccounts(), c), ct);
        bBanks.ShouldBeEmpty();
        var bSettings = await DispatchAsync(orgB, (s, c) => s.Query(new GetOrgSettings(), c), ct);
        bSettings.AccountingBasis.ShouldBe("cash");
    }

    [Fact]
    public async Task Effective_fee_resolves_property_override_over_owner_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await NewOrgAsync(ct);

        var ownerId = UuidV7.NewId();
        var withOverride = UuidV7.NewId();
        var noOverride = UuidV7.NewId();

        await WithOrgScopeAsync(orgId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Set<Owner>().Add(new Owner { Id = ownerId, Name = "Owner", DefaultMgmtFeeBps = 800 });
            db.Set<Property>().Add(new Property { Id = withOverride, OwnerId = ownerId, Address = "Override", MgmtFeeBps = 750 });
            db.Set<Property>().Add(new Property { Id = noOverride, OwnerId = ownerId, Address = "Default" });
            await db.SaveChangesAsync(ct);

            var fees = sp.GetRequiredService<IManagementFeeConfig>();
            (await fees.GetEffectiveFeeBpsAsync(ownerId, withOverride, ct)).ShouldBe(750); // property override wins
            (await fees.GetEffectiveFeeBpsAsync(ownerId, noOverride, ct)).ShouldBe(800);   // falls to owner default
            (await fees.GetEffectiveFeeBpsAsync(ownerId, null, ct)).ShouldBe(800);          // no property → owner default
        }, ct);
    }

    private async Task<Guid> NewOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Settings Org {orgId:N}" });
        await migratorDb.SaveChangesAsync(ct);
        return orgId;
    }

    private async Task WithOrgScopeAsync(Guid orgId, Func<IServiceProvider, Task> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        await executor.RunAsync(orgId, () => work(scope.ServiceProvider), ct);
    }

    private async Task<T> DispatchAsync<T>(Guid orgId, Func<ISender, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        T result = default!;
        await WithOrgScopeAsync(orgId, async sp => result = await work(sp.GetRequiredService<ISender>(), ct), ct);
        return result;
    }
}
