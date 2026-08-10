using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Runs;
using Shouldly;

namespace LeaseBook.Tests.Operations;

/// <summary>
/// Pins the four finished source-ref formats from ADR-019 §2. The prefixes are deliberately not
/// derived from RunType: "latefee" and "disbursement-fee" are not mechanical run-type names.
/// </summary>
public sealed class RunStrategySourceRefTests
{
    [Fact]
    public async Task Plans_use_the_four_ADR019_source_ref_formats()
    {
        var ct = TestContext.Current.CancellationToken;
        var period = new RunPeriod(2026, 3);

        var rentRow = Lease();
        var rentPlan = await new RentRunStrategy(
                new StubLeaseScheduleData([rentRow]),
                new StubPostedSourceRefs(),
                new StubPeriodChargeGuard())
            .PlanAsync(period, [rentRow.LeaseId], ct);

        var lateFeeRow = DelinquentLease();
        var lateFeePlan = await new LateFeeRunStrategy(
                new StubDelinquencyData([lateFeeRow]),
                new StubLateFeePolicyData(new Dictionary<Guid, LateFeePolicy>
                {
                    [lateFeeRow.LeaseId] = new(1, 5, LateFeeKind.Flat, 25m, 0),
                }),
                new StubPostedSourceRefs(),
                new StubPeriodChargeGuard())
            .PlanAsync(period, [lateFeeRow.LeaseId], ct);

        var owner = new OwnerDisbursementRow(Guid.NewGuid(), "Katherine Owner", 100m, 800);
        var disbursementPlan = await new DisbursementRunStrategy(
                new StubOwnerDisbursementData([owner]),
                new StubOwnerEquityBalances(new Dictionary<Guid, decimal> { [owner.OwnerId] = 1_000m }),
                new StubBankAccountInfo(),
                new StubPostedSourceRefs())
            .PlanAsync(period, [owner.OwnerId], ct);

        var rent = rentPlan.ShouldHaveSingleItem().ShouldBeOfType<PlannedPosting>()
            .Intent.ShouldBeOfType<RentChargeIntent>();
        var lateFee = lateFeePlan.ShouldHaveSingleItem().ShouldBeOfType<PlannedPosting>()
            .Intent.ShouldBeOfType<LateFeeIntent>();
        var disbursement = disbursementPlan.ShouldHaveSingleItem().ShouldBeOfType<PlannedPosting>()
            .Intent.ShouldBeOfType<DisbursementIntent>();

        rent.SourceRef.ShouldBe($"rent:2026-03:lease={rentRow.LeaseId}");
        lateFee.SourceRef.ShouldBe($"latefee:2026-03:lease={lateFeeRow.LeaseId}");
        disbursement.FeeSourceRef.ShouldBe($"disbursement-fee:2026-03:owner={owner.OwnerId}");
        disbursement.DisburseSourceRef.ShouldBe($"disbursement:2026-03:owner={owner.OwnerId}");
    }

    private static LeaseScheduleRow Lease() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Dorothy Tenant",
            "3C",
            1_200m,
            new DateOnly(2025, 1, 1),
            new DateOnly(2027, 12, 31));

    private static DelinquentLedgerRow DelinquentLease() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Mary Tenant",
            "4D",
            1_200m,
            1_200m,
            6);
}
