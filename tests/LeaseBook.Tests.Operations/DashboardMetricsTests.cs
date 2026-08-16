using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Features.Dashboard;
using Shouldly;

namespace LeaseBook.Tests.Operations;

public sealed class DashboardMetricsTests
{
    [Fact]
    public async Task Available_to_disburse_applies_management_fee_then_reserve_floor()
    {
        var ct = TestContext.Current.CancellationToken;
        var eligibleOwner = Guid.NewGuid();
        var belowFloorOwner = Guid.NewGuid();
        var service = new DashboardMetricsService(
            new StubOwnerDisbursementData(
            [
                new OwnerDisbursementRow(eligibleOwner, "Eligible", 200m, 800),
                new OwnerDisbursementRow(belowFloorOwner, "Below floor", 200m, 800),
            ]),
            new StubOwnerEquityBalances(new Dictionary<Guid, decimal>
            {
                [eligibleOwner] = 1_000m,
                [belowFloorOwner] = 150m,
            }),
            new StubLeaseScheduleData([]));

        var metrics = await service.GetAsync(2026, 6, ct);

        metrics.AvailableToDisburse.ShouldBe(720m); // 1,000 - 80 fee - 200 reserve
        metrics.OwnersAvailableToDisburse.ShouldBe(1);
    }

    [Fact]
    public async Task Scheduled_rent_is_the_periods_prorated_billing_baseline()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new DashboardMetricsService(
            new StubOwnerDisbursementData([]),
            new StubOwnerEquityBalances(new Dictionary<Guid, decimal>()),
            new StubLeaseScheduleData(
            [
                Lease(3_000m, new DateOnly(2026, 6, 1), null),
                Lease(3_000m, new DateOnly(2026, 6, 16), null),
            ]));

        var metrics = await service.GetAsync(2026, 6, ct);

        metrics.ScheduledRent.ShouldBe(4_500m);
    }

    private static LeaseScheduleRow Lease(decimal rent, DateOnly? start, DateOnly? end) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Tenant", "1A", rent, 1, start, end);
}
