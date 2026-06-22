using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

[Collection(nameof(DatabaseCollection))]
public sealed class BankDeactivationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Deactivating_a_bank_with_uncleared_items_is_blocked()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await executor.RunAsync(DemoSeeder.DemoOrgId, async () =>
        {
            var banks = await sender.Query(new ListBankAccounts(), ct);
            var operatingTrust = banks.Single(b => b.Name == "Operating Trust"); // has 3 uncleared
            var result = await sender.Send(new SetBankAccountActive(operatingTrust.Id, false), ct);
            result.Outcome.ShouldBe(SetActiveOutcome.BlockedUncleared);

            var after = await sender.Query(new ListBankAccounts(), ct);
            after.Single(b => b.Id == operatingTrust.Id).IsActive.ShouldBeTrue(); // unchanged
        }, ct);
    }

    [Fact]
    public async Task A_fully_cleared_bank_can_be_deactivated_and_reactivated_and_is_hidden_from_active_only()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await executor.RunAsync(DemoSeeder.DemoOrgId, async () =>
        {
            // Create a fresh bank with no journal lines → zero uncleared → deactivatable.
            var created = await sender.Send(new CreateBankAccount("Old Reserve", null, null, "operating"), ct);

            var off = await sender.Send(new SetBankAccountActive(created.Id, false), ct);
            off.Outcome.ShouldBe(SetActiveOutcome.Updated);
            off.Bank!.IsActive.ShouldBeFalse();

            (await sender.Query(new ListBankAccounts(ActiveOnly: true), ct))
                .ShouldNotContain(b => b.Id == created.Id);
            (await sender.Query(new ListBankAccounts(), ct))
                .ShouldContain(b => b.Id == created.Id); // still listed (Settings shows all)

            var on = await sender.Send(new SetBankAccountActive(created.Id, true), ct);
            on.Outcome.ShouldBe(SetActiveOutcome.Updated);
            on.Bank!.IsActive.ShouldBeTrue();
        }, ct);
    }

    [Fact]
    public async Task SetActive_on_unknown_id_returns_NotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await executor.RunAsync(DemoSeeder.DemoOrgId, async () =>
        {
            var result = await sender.Send(new SetBankAccountActive(Guid.NewGuid(), false), ct);
            result.Outcome.ShouldBe(SetActiveOutcome.NotFound);
        }, ct);
    }
}
