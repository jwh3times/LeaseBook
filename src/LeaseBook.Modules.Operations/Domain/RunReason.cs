namespace LeaseBook.Modules.Operations.Domain;

/// <summary>
/// Why a run did not post for a target: the reason every skipped or excluded item carries
/// (<c>CONTEXT.md</c>, <b>Run outcome</b>). Each reason fixes its own outcome, so a strategy names the
/// reason and never chooses a status beside it that could disagree.
/// </summary>
public enum RunReason
{
    /// <summary>A posting refusal: this run's source ref is already posted (a repeat run).</summary>
    DuplicateSourceRef,

    /// <summary>A posting refusal: the bank period is locked by a finalized reconciliation.</summary>
    PeriodLocked,

    /// <summary>A posting refusal: the accounting period is closed.</summary>
    PeriodClosed,

    /// <summary>A posting refusal: the disbursement would breach the owner's reserve floor.</summary>
    ReserveFloor,

    /// <summary>Rent is already charged for this tenant and period, by any route.</summary>
    AlreadyChargedInPeriod,

    /// <summary>The selected lease is not in the period's active schedule.</summary>
    LeaseNotInSchedule,

    /// <summary>The lease's rent is zero.</summary>
    RentZero,

    /// <summary>The selected lease is not delinquent for the period.</summary>
    LeaseNotDelinquent,

    /// <summary>No open rent obligation was found to attach a late fee to.</summary>
    RentObligationNotFound,

    /// <summary>No late-fee policy resolves for the lease.</summary>
    NoPolicy,

    /// <summary>The assessment date is before the lease's late-fee eligibility date.</summary>
    BeforeLateFeeEligibility,

    /// <summary>The selected owner was not found.</summary>
    OwnerNotFound,

    /// <summary>The owner's equity is zero or negative.</summary>
    NonPositiveEquity,

    /// <summary>After the fee, nothing is left above the owner's reserve.</summary>
    BelowReserveFloor,
}

/// <summary>
/// The one table of <see cref="RunReason"/> codes and outcomes. The code is what
/// <c>snapshot_json</c> stores under <c>reason</c> and what the API contract publishes, so it must
/// never change for a reason that committed runs already hold.
/// </summary>
public static class RunReasons
{
    private static readonly IReadOnlyDictionary<RunReason, (string Code, RunItemStatus Outcome)> Table =
        new Dictionary<RunReason, (string, RunItemStatus)>
        {
            [RunReason.DuplicateSourceRef] = ("duplicate_source_ref", RunItemStatus.Skipped),
            [RunReason.PeriodLocked] = ("period_locked", RunItemStatus.Excluded),
            [RunReason.PeriodClosed] = ("period_closed", RunItemStatus.Excluded),
            [RunReason.ReserveFloor] = ("reserve_floor", RunItemStatus.Excluded),
            [RunReason.AlreadyChargedInPeriod] = ("already_charged_in_period", RunItemStatus.Skipped),
            [RunReason.LeaseNotInSchedule] = ("lease_not_in_schedule", RunItemStatus.Excluded),
            [RunReason.RentZero] = ("rent_zero", RunItemStatus.Excluded),
            [RunReason.LeaseNotDelinquent] = ("lease_not_delinquent", RunItemStatus.Excluded),
            [RunReason.RentObligationNotFound] = ("rent_obligation_not_found", RunItemStatus.Excluded),
            [RunReason.NoPolicy] = ("no_policy", RunItemStatus.Excluded),
            [RunReason.BeforeLateFeeEligibility] = ("before_late_fee_eligibility", RunItemStatus.Excluded),
            [RunReason.OwnerNotFound] = ("owner_not_found", RunItemStatus.Excluded),
            [RunReason.NonPositiveEquity] = ("non_positive_equity", RunItemStatus.Excluded),
            [RunReason.BelowReserveFloor] = ("below_reserve_floor", RunItemStatus.Excluded),
        };

    private static readonly IReadOnlyDictionary<string, RunReason> ByCode =
        Table.ToDictionary(entry => entry.Value.Code, entry => entry.Key, StringComparer.Ordinal);

    /// <summary>Every code, in declaration order — the list the API contract publishes.</summary>
    public static IReadOnlyList<string> AllCodes { get; } =
        [.. Enum.GetValues<RunReason>().Select(Code)];

    /// <summary>The code stored and published for <paramref name="reason"/>.</summary>
    public static string Code(this RunReason reason) => Entry(reason).Code;

    /// <summary>The run outcome <paramref name="reason"/> means: skipped or excluded, never posted.</summary>
    public static RunItemStatus Outcome(this RunReason reason) => Entry(reason).Outcome;

    /// <summary>
    /// The reason a stored <paramref name="code"/> names. A code outside the table is refused: no run
    /// has recorded one, and guessing would show an operator a reason the run never gave.
    /// </summary>
    public static RunReason FromCode(string code) =>
        ByCode.TryGetValue(code, out var reason)
            ? reason
            : throw new InvalidOperationException($"'{code}' is not a run reason code.");

    private static (string Code, RunItemStatus Outcome) Entry(RunReason reason) =>
        Table.TryGetValue(reason, out var entry)
            ? entry
            : throw new InvalidOperationException($"Run reason {reason} has no code or outcome.");
}
