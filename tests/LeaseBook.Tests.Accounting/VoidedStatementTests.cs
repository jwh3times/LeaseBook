using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Statements;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// The WP-7 §0.3 defect pair, deterministic: an in-period void and an in-period opening position
/// must not reach StatementSectionMap (which throws on both by design — the map is the loud guard,
/// the SQL is the fix). See docs/superpowers/specs/2026-07-19-wp-7-import-supersede-held-fees-design.md §1.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class VoidedStatementTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_voided_charge_nets_inside_its_original_section()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var tenant = UuidV7.NewId();
        var owner = UuidV7.NewId();
        var property = UuidV7.NewId();
        await EnsureDirectoryAsync(fixture, scope, ct, owners: [owner], tenants: [tenant], properties: [property]);

        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            var chargeId = await events.PostAsync(
                new RentCharged(tenant, property, owner, null, new Money(100.00m), new DateOnly(2026, 5, 10), "rent"), ct);
            await Reversal(scope).ReverseAsync(chargeId, "test void", new DateOnly(2026, 5, 20), ct);

            var result = await new GetOwnerStatementDataHandler(scope.Db).Handle(
                new GetOwnerStatementData([owner], null, 2026, 5, "accrual"), ct);

            var s = result.ByOwner[owner];
            var income = s.Sections.Single(x => x.Key == StatementSectionKey.Income);
            income.Lines.Count.ShouldBe(2, "the charge and its void both belong to Income");
            income.Subtotal.ShouldBe(0m, "a voided charge nets to zero in place");
            s.TieOut.Variance.ShouldBe(0m);
            s.TieOut.Balanced.ShouldBeTrue();
        }, ct);
    }

    [Fact]
    public async Task An_in_period_opening_position_folds_into_beginning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var owner = UuidV7.NewId();
        await EnsureDirectoryAsync(fixture, scope, ct, owners: [owner]);

        await scope.RunAsync(async () =>
        {
            await Events(scope).PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.OwnerEquity, Debit: null, Credit: new Money(250.00m), EntryBasis.Both,
                Cutover: new DateOnly(2026, 5, 15), SourceRef: $"open:{owner}", OwnerId: owner), ct);

            var result = await new GetOwnerStatementDataHandler(scope.Db).Handle(
                new GetOwnerStatementData([owner], null, 2026, 5, "cash"), ct);

            var s = result.ByOwner[owner];
            s.Beginning.ShouldBe(250.00m, "an in-period opening position is Beginning, not a section line");
            s.Sections.ShouldBeEmpty("the opening entry must not appear as in-period movement");
            s.Ending.ShouldBe(250.00m);
            s.TieOut.Variance.ShouldBe(0m);
        }, ct);
    }
}
