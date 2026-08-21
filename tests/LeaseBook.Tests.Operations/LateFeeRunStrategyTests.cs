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

    [Theory]
    [InlineData(19, false)] // March 19 is late day 4 for rent due March 15.
    [InlineData(20, true)]  // March 20 is late day 5.
    public async Task Eligibility_counts_the_day_after_the_contractual_due_date_as_late_day_one(
        int assessmentDay,
        bool eligible)
    {
        var ct = TestContext.Current.CancellationToken;
        var leaseId = Guid.NewGuid();
        var strategy = BuildStrategy(
            [DelinquentLease(leaseId)],
            [(leaseId, new LateFeePolicy(15, 0, LateFeeKind.Flat, 50m, 0))],
            new DateOnly(2026, 3, assessmentDay));

        var plan = await strategy.PlanAsync(Period, [leaseId], ct);

        if (eligible)
        {
            plan.ShouldHaveSingleItem().ShouldBeOfType<PlannedPosting>();
        }
        else
        {
            var exclusion = plan.ShouldHaveSingleItem().ShouldBeOfType<PlannedExclusion>();
            exclusion.Detail["reason"].ShouldBe("before_late_fee_eligibility");
        }
    }

    [Fact]
    public async Task Plan_links_the_fee_to_the_specific_rent_obligation()
    {
        var ct = TestContext.Current.CancellationToken;
        var leaseId = Guid.NewGuid();
        var rentObligationEntryId = Guid.NewGuid();
        var row = DelinquentLease(leaseId) with
        {
            Attribution = new DelinquencyAttribution.AttributedToLease(rentObligationEntryId),
        };
        var strategy = BuildStrategy(
            [row],
            [(leaseId, new LateFeePolicy(1, 5, LateFeeKind.Flat, 50m, 0))],
            new DateOnly(2026, 3, 6));

        var plan = await strategy.PlanAsync(Period, [leaseId], ct);

        var intent = plan.ShouldHaveSingleItem().ShouldBeOfType<PlannedPosting>()
            .Intent.ShouldBeOfType<LateFeeIntent>();
        intent.RentObligationEntryId.ShouldBe(rentObligationEntryId);
        intent.SourceRef.ShouldBe($"latefee:rent-entry={rentObligationEntryId}");
        intent.Date.ShouldBe(new DateOnly(2026, 3, 6));
    }

    [Fact]
    public async Task Preview_uses_the_real_assessment_date_instead_of_month_end()
    {
        var ct = TestContext.Current.CancellationToken;
        var leaseId = Guid.NewGuid();
        var assessmentDate = new DateOnly(2026, 3, 3);
        var data = new RecordingDelinquencyData([DelinquentLease(leaseId)]);
        var strategy = new LateFeeRunStrategy(
            data,
            new StubLateFeePolicyData(new Dictionary<Guid, LateFeePolicy>
            {
                [leaseId] = new(15, 5, LateFeeKind.Flat, 50m, 0),
            }),
            new StubPostedSourceRefs(),
            new FixedTimeProvider(assessmentDate));

        var preview = await strategy.PreviewAsync(Period, ct);

        data.AsOf.ShouldBe(assessmentDate);
        preview.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void Attributed_delinquency_rejects_an_empty_rent_obligation_id()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new DelinquencyAttribution.AttributedToLease(Guid.Empty));
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
        var row = DelinquentLease(leaseId) with { Rent = monthlyRent };
        var strategy = BuildStrategy(
            [row],
            [(leaseId, new LateFeePolicy(1, 5, LateFeeKind.Flat, flatFee, 0))]);

        var plan = await strategy.PlanAsync(Period, [leaseId], ct);

        var posting = plan.ShouldHaveSingleItem().ShouldBeOfType<PlannedPosting>();
        posting.Amount.ShouldBe(expectedFee);
        posting.Intent.ShouldBeOfType<LateFeeIntent>().Amount.ShouldBe(expectedFee);
    }

    [Fact]
    public async Task Preview_honours_a_contractual_threshold_longer_than_five_days()
    {
        var ct = TestContext.Current.CancellationToken;
        var atBoundaryId = Guid.NewGuid();
        var pastBoundaryId = Guid.NewGuid();
        var strategy = BuildStrategy(
            [DelinquentLease(atBoundaryId), DelinquentLease(pastBoundaryId)],
            [
                (atBoundaryId, new LateFeePolicy(1, 7, LateFeeKind.Flat, 50m, 0)),
                (pastBoundaryId, new LateFeePolicy(1, 6, LateFeeKind.Flat, 50m, 0)),
            ],
            new DateOnly(2026, 3, 7));

        var preview = await strategy.PreviewAsync(Period, ct);

        preview.Rows.ShouldHaveSingleItem().TargetId.ShouldBe(pastBoundaryId);
        preview.Exceptions.ShouldHaveSingleItem().ShouldContain("threshold 7");
    }

    // The NC §42-46 floor used to be derived twice — once per path — and the only preview test of it
    // used grace days above 5, where the clamp is a no-op. Deleting Math.Max(5, …) from the preview
    // copy therefore left the whole suite green while the operator was shown a lease as chargeable
    // four days before it legally was (#199). These two drive the preview side of the same rules the
    // plan-side tests above drive.
    [Theory]
    [InlineData(19, false)] // March 19 is late day 4 for rent due March 15 — inside the statutory floor.
    [InlineData(20, true)]  // March 20 is late day 5.
    public async Task Preview_applies_the_statutory_floor_to_a_lease_that_grants_no_grace(
        int assessmentDay,
        bool eligible)
    {
        var ct = TestContext.Current.CancellationToken;
        var leaseId = Guid.NewGuid();
        var strategy = BuildStrategy(
            [DelinquentLease(leaseId)],
            [(leaseId, new LateFeePolicy(15, 0, LateFeeKind.Flat, 50m, 0))],
            new DateOnly(2026, 3, assessmentDay));

        var preview = await strategy.PreviewAsync(Period, ct);

        if (eligible)
        {
            preview.Rows.ShouldHaveSingleItem().TargetId.ShouldBe(leaseId);
            preview.Exceptions.ShouldBeEmpty();
        }
        else
        {
            // Not merely absent from the rows — the operator is told why, and cannot select it.
            preview.Rows.ShouldBeEmpty();
            preview.Exceptions.ShouldHaveSingleItem().ShouldContain("not eligible until 2026-03-20");
        }
    }

    [Theory]
    [InlineData(19)] // Inside the statutory floor.
    [InlineData(20)] // Past it.
    public async Task Preview_and_plan_reach_the_same_verdict_for_the_same_lease(int assessmentDay)
    {
        // The property #199 is really about: one rule set, two projections. A lease the preview
        // refuses to offer must be a lease the plan refuses to post, and vice versa — on identical
        // data. Re-splitting the two paths fails here even if each side stays self-consistent.
        var ct = TestContext.Current.CancellationToken;
        var leaseId = Guid.NewGuid();
        var strategy = BuildStrategy(
            [DelinquentLease(leaseId)],
            [(leaseId, new LateFeePolicy(15, 0, LateFeeKind.Flat, 50m, 0))],
            new DateOnly(2026, 3, assessmentDay));

        var preview = await strategy.PreviewAsync(Period, ct);
        var plan = await strategy.PlanAsync(Period, [leaseId], ct);

        var offeredInPreview = preview.Rows.Count == 1;
        var postedByPlan = plan.ShouldHaveSingleItem() is PlannedPosting;
        postedByPlan.ShouldBe(offeredInPreview);
    }

    [Fact]
    public async Task A_lease_with_no_resolvable_policy_is_refused_by_both_projections()
    {
        // Neither projection of this branch had any coverage before #199 — it could have been
        // deleted outright without turning the suite red. A missing policy means the fee has no
        // defined amount or due day, so it must never be offered and never be posted.
        var ct = TestContext.Current.CancellationToken;
        var leaseId = Guid.NewGuid();
        var strategy = BuildStrategy([DelinquentLease(leaseId)], policies: []);

        var preview = await strategy.PreviewAsync(Period, ct);
        var plan = await strategy.PlanAsync(Period, [leaseId], ct);

        preview.Rows.ShouldBeEmpty();
        preview.Exceptions.ShouldHaveSingleItem().ShouldContain("no late-fee policy found");

        var exclusion = plan.ShouldHaveSingleItem().ShouldBeOfType<PlannedExclusion>();
        exclusion.Status.ShouldBe(RunItemStatus.Excluded);
        exclusion.Detail["reason"].ShouldBe("no_policy");
    }

    private static LateFeeRunStrategy BuildStrategy(
        IReadOnlyList<DelinquentLedgerRow> rows,
        IReadOnlyList<(Guid LeaseId, LateFeePolicy Policy)> policies,
        DateOnly? assessmentDate = null) =>
        new(
            new StubDelinquencyData(rows),
            new StubLateFeePolicyData(policies.ToDictionary(x => x.LeaseId, x => x.Policy)),
            new StubPostedSourceRefs(),
            new FixedTimeProvider(assessmentDate ?? new DateOnly(2026, 3, 7)));

    private static DelinquentLedgerRow DelinquentLease(Guid leaseId) =>
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
            Attribution: new DelinquencyAttribution.AttributedToLease(Guid.NewGuid()));

    private sealed class FixedTimeProvider(DateOnly date) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    private sealed class RecordingDelinquencyData(IReadOnlyList<DelinquentLedgerRow> rows)
        : IDelinquencyData
    {
        public DateOnly? AsOf { get; private set; }

        public Task<IReadOnlyList<DelinquentLedgerRow>> GetAsync(
            int year,
            int month,
            DateOnly asOf,
            CancellationToken ct)
        {
            AsOf = asOf;
            return Task.FromResult(rows);
        }
    }
}
