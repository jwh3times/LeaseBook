using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Statements;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// #377: the owner-equity footprint of specific entries — what an owner statement can see of them, and
/// therefore what decides whether an issued statement will carry them forward.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OwnerEquityLinesTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_charge_reports_its_owner_property_basis_date_and_posting_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, tenant, property) = (UuidV7.NewId(), UuidV7.NewId(), UuidV7.NewId());
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await EnsureDirectoryAsync(fixture, scope, ct, owners: [owner], tenants: [tenant], properties: [property]);

        await scope.RunAsync(async () =>
        {
            var before = DateTime.UtcNow;
            var charge = await Events(scope).PostAsync(
                new RentCharged(tenant, property, owner, null, new Money(125.00m), new DateOnly(2026, 5, 10), "rent"), ct);

            var lines = await new GetOwnerEquityLinesHandler(scope.Db).Handle(new GetOwnerEquityLines([charge]), ct);

            var line = lines.ShouldHaveSingleItem("rent charges owner equity once, on the accrual basis");
            line.EntryId.ShouldBe(charge);
            line.OwnerId.ShouldBe(owner);
            line.PropertyId.ShouldBe(property);
            line.Basis.ShouldBe("accrual");
            line.EntryDate.ShouldBe(new DateOnly(2026, 5, 10));
            line.Amount.ShouldBe(125.00m);
            line.PostedAt.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-1));
        }, ct);
    }

    [Fact]
    public async Task A_void_reports_the_mirrored_movement_and_unknown_ids_report_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, tenant, property) = (UuidV7.NewId(), UuidV7.NewId(), UuidV7.NewId());
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await EnsureDirectoryAsync(fixture, scope, ct, owners: [owner], tenants: [tenant], properties: [property]);

        await scope.RunAsync(async () =>
        {
            var charge = await Events(scope).PostAsync(
                new RentCharged(tenant, property, owner, null, new Money(80.00m), new DateOnly(2026, 5, 10), "rent"), ct);
            var reversal = await Reversal(scope).ReverseAsync(charge, "test void", new DateOnly(2026, 6, 2), ct);

            var lines = await new GetOwnerEquityLinesHandler(scope.Db).Handle(
                new GetOwnerEquityLines([reversal, UuidV7.NewId()]), ct);

            var line = lines.ShouldHaveSingleItem("an id with no owner-equity lines contributes nothing");
            line.EntryId.ShouldBe(reversal);
            line.Basis.ShouldBe("accrual");
            line.EntryDate.ShouldBe(new DateOnly(2026, 6, 2));
            line.Amount.ShouldBe(-80.00m);
        }, ct);
    }
}
