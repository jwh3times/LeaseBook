using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Operations.Runs;
using Shouldly;

namespace LeaseBook.Tests.Operations;

public sealed class LateFeeRunStrategyTests
{
    private static readonly RunPeriod Period = new(2026, 3);

    public static TheoryData<decimal, decimal, decimal> ClampCases => new()
    {
        { 1_450m, 200m, 72.50m },
        { 200m, 200m, 15.00m },
    };

    [Fact]
    public void Attributed_delinquency_rejects_negative_days_late()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new DelinquencyAttribution.AttributedToLease(-1));
    }

    [Fact]
    public async Task Plan_excludes_a_selected_lease_at_the_grace_boundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var leaseId = Guid.NewGuid();
        var row = DelinquentLease(leaseId, daysLate: 5);
        var strategy = BuildStrategy(
            [row],
            [(leaseId, new LateFeePolicy(1, 5, LateFeeKind.Flat, 50m, 0))]);

        var plan = await strategy.PlanAsync(Period, [leaseId], ct);

        var exclusion = plan.ShouldHaveSingleItem().ShouldBeOfType<PlannedExclusion>();
        exclusion.Status.ShouldBe(RunItemStatus.Excluded);
        exclusion.Detail["reason"].ShouldBe("within_grace_period");
    }

    [Fact]
    public async Task Plan_excludes_a_selected_lease_whose_delinquency_is_ambiguous()
    {
        var ct = TestContext.Current.CancellationToken;
        var leaseId = Guid.NewGuid();
        var row = AmbiguousDelinquentLease(leaseId);
        var strategy = BuildStrategy(
            [row],
            [(leaseId, new LateFeePolicy(1, 5, LateFeeKind.Flat, 50m, 0))]);

        var plan = await strategy.PlanAsync(Period, [leaseId], ct);

        var exclusion = plan.ShouldHaveSingleItem().ShouldBeOfType<PlannedExclusion>();
        exclusion.Status.ShouldBe(RunItemStatus.Excluded);
        exclusion.Detail["reason"].ShouldBe("ambiguous_multiple_active_leases");
    }

    [Theory]
    [MemberData(nameof(ClampCases))]
    public async Task Plan_applies_the_NC_statutory_clamp(
        decimal monthlyRent,
        decimal flatFee,
        decimal expectedFee)
    {
        var ct = TestContext.Current.CancellationToken;
        var leaseId = Guid.NewGuid();
        var row = DelinquentLease(leaseId, daysLate: 6) with { Rent = monthlyRent };
        var strategy = BuildStrategy(
            [row],
            [(leaseId, new LateFeePolicy(1, 5, LateFeeKind.Flat, flatFee, 0))]);

        var plan = await strategy.PlanAsync(Period, [leaseId], ct);

        var posting = plan.ShouldHaveSingleItem().ShouldBeOfType<PlannedPosting>();
        posting.Amount.ShouldBe(expectedFee);
        posting.Intent.ShouldBeOfType<LateFeeIntent>().Amount.ShouldBe(expectedFee);
    }

    [Fact]
    public async Task Preview_includes_only_leases_strictly_past_the_grace_boundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var atBoundaryId = Guid.NewGuid();
        var pastBoundaryId = Guid.NewGuid();
        var policy = new LateFeePolicy(1, 5, LateFeeKind.Flat, 50m, 0);
        var strategy = BuildStrategy(
            [DelinquentLease(atBoundaryId, daysLate: 5), DelinquentLease(pastBoundaryId, daysLate: 6)],
            [(atBoundaryId, policy), (pastBoundaryId, policy)]);

        var preview = await strategy.PreviewAsync(Period, ct);

        preview.Rows.ShouldHaveSingleItem().TargetId.ShouldBe(pastBoundaryId);
        preview.Exceptions.ShouldHaveSingleItem().ShouldContain("within the grace period");
    }

    private static LateFeeRunStrategy BuildStrategy(
        IReadOnlyList<DelinquentLedgerRow> rows,
        IReadOnlyList<(Guid LeaseId, LateFeePolicy Policy)> policies) =>
        new(
            new StubDelinquencyData(rows),
            new StubLateFeePolicyData(policies.ToDictionary(x => x.LeaseId, x => x.Policy)),
            new StubPostedSourceRefs(),
            new StubPeriodChargeGuard());

    private static DelinquentLedgerRow DelinquentLease(Guid leaseId, int daysLate) =>
        new(
            LeaseId: leaseId,
            TenantId: Guid.NewGuid(),
            PropertyId: Guid.NewGuid(),
            OwnerId: Guid.NewGuid(),
            UnitId: Guid.NewGuid(),
            TenantName: "Ada Tenant",
            UnitLabel: "1A",
            Rent: 1_000m,
            Balance: 1_000m,
            Attribution: new DelinquencyAttribution.AttributedToLease(daysLate));

    private static DelinquentLedgerRow AmbiguousDelinquentLease(Guid leaseId) =>
        DelinquentLease(leaseId, daysLate: 0) with
        {
            Attribution = new DelinquencyAttribution.AmbiguousMultipleActiveLeases(),
        };
}
