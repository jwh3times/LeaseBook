using LeaseBook.Modules.Operations.Domain;
using Shouldly;

namespace LeaseBook.Tests.Operations;

/// <summary>
/// The reason a run records for a target it did not post (#573). The codes are stored in every
/// skipped and excluded item's <c>snapshot_json</c> since M6 and read back by the run-detail endpoint,
/// so a renamed code would make every older run unreadable: the table below is written out by hand,
/// from the codes committed runs already hold, and is never derived from the code under test.
/// </summary>
public sealed class RunReasonsTests
{
    private static readonly (RunReason Reason, string Code, RunItemStatus Outcome)[] Pinned =
    [
        // Posting refusals, classified by the run engine.
        (RunReason.DuplicateSourceRef, "duplicate_source_ref", RunItemStatus.Skipped),
        (RunReason.PeriodLocked, "period_locked", RunItemStatus.Excluded),
        (RunReason.PeriodClosed, "period_closed", RunItemStatus.Excluded),
        (RunReason.ReserveFloor, "reserve_floor", RunItemStatus.Excluded),

        // Decided by a run type before anything posts.
        (RunReason.AlreadyChargedInPeriod, "already_charged_in_period", RunItemStatus.Skipped),
        (RunReason.LeaseNotInSchedule, "lease_not_in_schedule", RunItemStatus.Excluded),
        (RunReason.RentZero, "rent_zero", RunItemStatus.Excluded),
        (RunReason.LeaseNotDelinquent, "lease_not_delinquent", RunItemStatus.Excluded),
        (RunReason.RentObligationNotFound, "rent_obligation_not_found", RunItemStatus.Excluded),
        (RunReason.NoPolicy, "no_policy", RunItemStatus.Excluded),
        (RunReason.BeforeLateFeeEligibility, "before_late_fee_eligibility", RunItemStatus.Excluded),
        (RunReason.OwnerNotFound, "owner_not_found", RunItemStatus.Excluded),
        (RunReason.NonPositiveEquity, "non_positive_equity", RunItemStatus.Excluded),
        (RunReason.BelowReserveFloor, "below_reserve_floor", RunItemStatus.Excluded),
    ];

    public static TheoryData<RunReason, string, RunItemStatus> Recorded =>
        new(Pinned.Select(row => (row.Reason, row.Code, row.Outcome)));

    [Theory]
    [MemberData(nameof(Recorded))]
    public void Each_reason_keeps_the_code_stored_runs_hold_and_the_outcome_it_means(
        RunReason reason, string code, RunItemStatus outcome)
    {
        reason.Code().ShouldBe(code);
        reason.Outcome().ShouldBe(outcome);
        RunReasons.FromCode(code).ShouldBe(reason);
    }

    [Fact]
    public void Every_reason_is_in_the_published_list_and_no_other_code_is()
    {
        var pinned = Pinned.Select(row => row.Code).ToList();

        Enum.GetValues<RunReason>().Length.ShouldBe(pinned.Count,
            "a reason added without a pinned code and outcome above has had no decision about either");
        RunReasons.AllCodes.ShouldBe(pinned, ignoreOrder: true);
        RunReasons.AllCodes.Distinct().Count().ShouldBe(RunReasons.AllCodes.Count);
    }

    [Fact]
    public void No_reason_means_posted()
    {
        Enum.GetValues<RunReason>().ShouldAllBe(reason => reason.Outcome() != RunItemStatus.Posted);
    }

    [Fact]
    public void A_code_no_run_ever_recorded_is_refused_rather_than_guessed()
    {
        Should.Throw<InvalidOperationException>(() => RunReasons.FromCode("zero_rent"));
    }
}
