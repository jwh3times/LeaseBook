using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Operations.Runs;
using Shouldly;

namespace LeaseBook.Tests.Operations;

public sealed class RentRunStrategyTests
{
    private static readonly RunPeriod Period = new(2026, 6);

    [Fact]
    public async Task Plan_carries_the_contractual_due_date_separately_from_the_posting_date()
    {
        var ct = TestContext.Current.CancellationToken;
        var row = ActiveLease(rent: 1_000m) with { RentDueDay = 15 };
        var strategy = BuildStrategy([row]);

        var plan = await strategy.PlanAsync(Period, [row.LeaseId], ct);

        var intent = plan.ShouldHaveSingleItem().ShouldBeOfType<PlannedPosting>()
            .Intent.ShouldBeOfType<RentChargeIntent>();
        intent.Date.ShouldBe(new DateOnly(2026, 6, 1));
        intent.DueDate.ShouldBe(new DateOnly(2026, 6, 15));
    }

    [Fact]
    public async Task Plan_turns_an_eligible_selected_lease_into_a_prorated_rent_intent()
    {
        var ct = TestContext.Current.CancellationToken;
        var row = ActiveLease(rent: 1_450m, startDate: new DateOnly(2026, 6, 14));
        var strategy = BuildStrategy([row]);

        var plan = await strategy.PlanAsync(Period, [row.LeaseId], ct);

        var posting = plan.ShouldHaveSingleItem().ShouldBeOfType<PlannedPosting>();
        posting.TargetKind.ShouldBe(RunTargetKind.Lease);
        posting.TargetId.ShouldBe(row.LeaseId);
        posting.Amount.ShouldBe(821.67m);
        var intent = posting.Intent.ShouldBeOfType<RentChargeIntent>();
        intent.Amount.ShouldBe(821.67m);
        intent.Date.ShouldBe(new DateOnly(2026, 6, 1));
        intent.Description.ShouldContain("(prorated)");
        posting.PostedDetail["prorated"].ShouldBe(true);
    }

    [Fact]
    public async Task Plan_records_each_selected_ineligible_lease_without_a_posting()
    {
        var ct = TestContext.Current.CancellationToken;
        var zeroRent = ActiveLease(rent: 0m);
        var alreadyCharged = ActiveLease(rent: 1_000m);
        var missingLeaseId = Guid.NewGuid();
        var strategy = BuildStrategy(
            [zeroRent, alreadyCharged],
            new HashSet<Guid> { alreadyCharged.TenantId });

        var plan = await strategy.PlanAsync(
            Period,
            [missingLeaseId, zeroRent.LeaseId, alreadyCharged.LeaseId],
            ct);

        plan.Select(Reason).ShouldBe(
            ["lease_not_in_schedule", "rent_zero", "already_charged_in_period"]);
        plan.ShouldAllBe(item => item is PlannedExclusion);
    }

    private static RentRunStrategy BuildStrategy(
        IReadOnlyList<LeaseScheduleRow> rows,
        IReadOnlySet<Guid>? chargedTenants = null) =>
        new(
            new StubLeaseScheduleData(rows),
            new StubPostedSourceRefs(),
            new StubPeriodChargeGuard(chargedTenants));

    private static LeaseScheduleRow ActiveLease(
        decimal rent,
        DateOnly? startDate = null,
        DateOnly? endDate = null) =>
        new(
            LeaseId: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            PropertyId: Guid.NewGuid(),
            OwnerId: Guid.NewGuid(),
            UnitId: Guid.NewGuid(),
            TenantName: "Grace Hopper",
            UnitLabel: "2B",
            Rent: rent,
            RentDueDay: 1,
            StartDate: startDate,
            EndDate: endDate);

    private static string Reason(RunPlanItem item) =>
        (string)((PlannedExclusion)item).Detail["reason"]!;
}
