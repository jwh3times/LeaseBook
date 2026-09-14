using FluentValidation;
using LeaseBook.Modules.Accounting;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Statements;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// #380: a run preview sees exactly the owner-equity footprint that the real posting path writes.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ProspectiveOwnerEquityLinesTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RentCharged_preview_matches_the_posted_owner_equity_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, tenant, property) = (UuidV7.NewId(), UuidV7.NewId(), UuidV7.NewId());
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await EnsureDirectoryAsync(fixture, scope, ct, owners: [owner], tenants: [tenant], properties: [property]);
        var businessEvent = new RentCharged(
            tenant, property, owner, null, new Money(125.00m), new DateOnly(2026, 5, 10), "rent");

        await AssertPreviewMatchesPostedAsync(scope, businessEvent, ct);
    }

    [Fact]
    public async Task FeeCharged_preview_matches_the_posted_owner_equity_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, tenant, property) = (UuidV7.NewId(), UuidV7.NewId(), UuidV7.NewId());
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await EnsureDirectoryAsync(fixture, scope, ct, owners: [owner], tenants: [tenant], properties: [property]);
        var businessEvent = new FeeCharged(
            tenant, property, owner, null, new Money(35.00m), new DateOnly(2026, 5, 11),
            FeeKind.Late, "late fee");

        await AssertPreviewMatchesPostedAsync(scope, businessEvent, ct);
    }

    [Fact]
    public async Task ManagementFeeAssessed_preview_matches_the_posted_owner_equity_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, property) = (UuidV7.NewId(), UuidV7.NewId());
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await EnsureDirectoryAsync(fixture, scope, ct, owners: [owner], properties: [property]);
        var businessEvent = new ManagementFeeAssessed(
            owner, property, new Money(29.00m), new DateOnly(2026, 5, 31), scope.TrustBankId,
            "management fee");

        await AssertPreviewMatchesPostedAsync(scope, businessEvent, ct);
    }

    [Fact]
    public async Task OwnerDisbursed_preview_matches_the_posted_owner_equity_line()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, property) = (UuidV7.NewId(), UuidV7.NewId());
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await EnsureDirectoryAsync(fixture, scope, ct, owners: [owner], properties: [property]);
        await scope.RunAsync(() => Events(scope).PostAsync(new OwnerContribution(
            owner, property, new Money(500.00m), new DateOnly(2026, 5, 1), scope.TrustBankId, "funding"), ct), ct);
        var businessEvent = new OwnerDisbursed(
            owner, new Money(400.00m), new DateOnly(2026, 5, 31), scope.TrustBankId,
            "owner distribution", Reserve: new Money(100.00m));

        await AssertPreviewMatchesPostedAsync(scope, businessEvent, ct);
    }

    [Fact]
    public async Task An_event_outside_the_run_catalog_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var unsupported = new OwnerContribution(
            UuidV7.NewId(), null, new Money(10.00m), new DateOnly(2026, 5, 1), scope.TrustBankId, "funding");

        await scope.RunAsync(async () =>
        {
            var sender = CreateSender(scope);

            var exception = await Should.ThrowAsync<ValidationException>(() =>
                sender.Query(new GetProspectiveOwnerEquityLines([unsupported]), ct));

            exception.Errors.ShouldContain(error => error.PropertyName == nameof(GetProspectiveOwnerEquityLines.Events));
        }, ct);
    }

    [Fact]
    public async Task No_events_have_no_prospective_owner_equity_lines()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        await scope.RunAsync(async () =>
        {
            var lines = await CreateSender(scope).Query(
                new GetProspectiveOwnerEquityLines([]), ct);

            lines.ShouldBeEmpty();
        }, ct);
    }

    private static async Task AssertPreviewMatchesPostedAsync(
        OrgScope scope,
        AccountingEvent businessEvent,
        CancellationToken ct)
    {
        await scope.RunAsync(async () =>
        {
            var sender = CreateSender(scope);
            var prospective = await sender.Query(new GetProspectiveOwnerEquityLines([businessEvent]), ct);
            var entryId = await Events(scope).PostAsync(businessEvent, ct);
            var posted = await sender.Query(new GetOwnerEquityLines([entryId]), ct);

            prospective.ShouldBe(posted.Select(line => new ProspectiveOwnerEquityLine(
                line.OwnerId, line.PropertyId, line.Basis, line.EntryDate, line.Amount)));
        }, ct);
    }

    private static ISender CreateSender(OrgScope scope)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<DbContext>(_ => scope.Db);
        services.AddLeaseBookCqrs(typeof(AccountingModuleServiceCollectionExtensions).Assembly);
        return services.BuildServiceProvider().GetRequiredService<ISender>();
    }
}
