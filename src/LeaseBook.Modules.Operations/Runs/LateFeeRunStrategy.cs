using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// <see cref="IRunStrategy"/> for <see cref="RunType.LateFee"/>. Previews the monthly late-fee charge
/// run and plans it, applying the NC §42-46 statutory clamp via <see cref="LateFeeCalculator"/>.
/// <para>
/// <b>Source-ref convention (ADR-033):</b> <c>latefee:rent-entry={rentEntryId}</c>. The key
/// deduplicates repeat runs while the journal's unique <c>assesses_entry_id</c> relationship enforces
/// one assessment per rent obligation even under a different source reference.
/// </para>
/// <para>
/// <b>Assessment and charge date:</b> the server's current UTC calendar date. The operator supplies
/// no date, so a future-dated assessment is not representable. Confirm recalculates and posts on its
/// own assessment date.
/// </para>
/// <para>
/// <b>Delinquency signal:</b> the <see cref="IDelinquencyData"/> port provides per-lease
/// receivable balances and the canonical, unreversed rent obligation. Eligibility comes from the
/// effective policy's contractual due date: the following day is late day one, and the first
/// permitted assessment is due date plus <c>max(5, contractual threshold)</c>.
/// </para>
/// <para>
/// <b>Eligibility exclusions:</b> preview surfaces these as exceptions rather than chargeable rows;
/// planning re-establishes the same rules from its confirmation-time data and records a selected target as
/// <see cref="RunItemStatus.Excluded"/> rather than posting it.
/// <list type="bullet">
///   <item>Lease before its late-fee eligibility date.</item>
///   <item>Lease whose period has no canonical, unreversed, open rent obligation.</item>
///   <item>Lease with no effective policy resolved.</item>
/// </list>
/// A locked bank period or a closed accounting period comes back from the posting port as a refusal
/// and the engine records it per-item as <see cref="RunItemStatus.Excluded"/>; neither is visible
/// here.
/// </para>
/// </summary>
public sealed class LateFeeRunStrategy(
    IDelinquencyData delinquency,
    ILateFeePolicyData policies,
    IPostedSourceRefs postedRefs,
    TimeProvider clock) : IRunStrategy
{
    /// <inheritdoc />
    public RunType RunType => RunType.LateFee;

    /// <inheritdoc />
    public async Task<StrategyPreview> PreviewAsync(RunPeriod period, CancellationToken ct)
    {
        var assessmentDate = CurrentDate();

        // Fetch delinquent leases and their effective policies in parallel.
        var delinquentRows = await delinquency.GetAsync(period.Year, period.Month, assessmentDate, ct);

        if (delinquentRows.Count == 0)
        {
            return new StrategyPreview([], []);
        }

        var leaseIds = delinquentRows.Select(r => r.LeaseId).ToList();

        // Fetch effective policies, then the already-posted obligation keys.
        var policyMap = await policies.GetAsync(leaseIds, ct);

        var allKeys = delinquentRows
            .Select(r => r.Attribution)
            .OfType<DelinquencyAttribution.AttributedToLease>()
            .Select(a => SourceRef(a.RentObligationEntryId))
            .ToList();
        var alreadyPosted = await BatchRead.SetOrEmptyAsync(
            allKeys, keys => postedRefs.GetExistingAsync(keys, ct));

        var previewRows = new List<PreviewRow>(delinquentRows.Count);
        var exceptions = new List<string>();

        foreach (var row in delinquentRows)
        {
            policyMap.TryGetValue(row.LeaseId, out var policy);

            var decision = Decide(row, policy, assessmentDate, period);
            if (decision is IneligibleForLateFee ineligible)
            {
                // Preview surfaces an ineligible lease as prose and omits the row entirely, so it
                // cannot be selected. The wording is the decision's, not this loop's.
                exceptions.Add(ineligible.Explanation);
                continue;
            }

            var charge = (ChargeLateFee)decision;

            var detail = new Dictionary<string, string>
            {
                ["unit"] = row.UnitLabel,
                ["balance"] = row.Balance.ToString("F2"),
                ["daysLate"] = charge.DaysLate.ToString(),
                ["dueDate"] = charge.DueDate.ToString("yyyy-MM-dd"),
                ["assessmentDate"] = assessmentDate.ToString("yyyy-MM-dd"),
                ["eligibilityDate"] = charge.EligibilityDate.ToString("yyyy-MM-dd"),
                ["feeKind"] = charge.Kind.ToString(),
                ["monthlyRent"] = row.Rent.ToString("F2"),
            };

            previewRows.Add(new PreviewRow(
                TargetKind: RunTargetKind.Lease,
                TargetId: row.LeaseId,
                Label: row.TenantName,
                Amount: charge.Amount,
                AlreadyDone: alreadyPosted.Contains(SourceRef(charge.RentObligationEntryId)),
                ExcludedReason: null,
                Detail: detail));
        }

        return new StrategyPreview(previewRows, exceptions);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunPlanItem>> PlanAsync(
        RunPeriod period,
        IReadOnlyList<Guid> selectedTargetIds,
        CancellationToken ct)
    {
        var assessmentDate = CurrentDate();
        var allDelinquent = await delinquency.GetAsync(period.Year, period.Month, assessmentDate, ct);
        var byLeaseId = allDelinquent.ToDictionary(r => r.LeaseId);

        // Fetch effective policies for selected leases.
        var selectedInSchedule = selectedTargetIds
            .Where(id => byLeaseId.ContainsKey(id))
            .ToList();
        var policyMap = await BatchRead.MapOrEmptyAsync(
            selectedInSchedule, ids => policies.GetAsync(ids, ct));

        var plan = new List<RunPlanItem>(selectedTargetIds.Count);

        foreach (var leaseId in selectedTargetIds)
        {
            if (!byLeaseId.TryGetValue(leaseId, out var row))
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Excluded, "lease_not_delinquent"));
                continue;
            }

            policyMap.TryGetValue(leaseId, out var policy);

            // The same decision the preview projected, re-derived from confirmation-time data. The
            // rules are not restated here; only the recording vocabulary differs.
            var decision = Decide(row, policy, assessmentDate, period);
            if (decision is IneligibleForLateFee ineligible)
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Excluded, ineligible.Code));
                continue;
            }

            var charge = (ChargeLateFee)decision;
            var amount = charge.Amount;
            var description = $"Late fee {period.Key} — {row.TenantName} {row.UnitLabel}";
            var sourceRef = SourceRef(charge.RentObligationEntryId);

            plan.Add(new PlannedPosting(
                TargetKind: RunTargetKind.Lease,
                TargetId: leaseId,
                Intent: new LateFeeIntent(
                    LeaseId: leaseId,
                    RentObligationEntryId: charge.RentObligationEntryId,
                    TenantId: row.TenantId,
                    PropertyId: row.PropertyId,
                    OwnerId: row.OwnerId,
                    UnitId: row.UnitId,
                    Amount: amount,
                    Date: assessmentDate,
                    Description: description,
                    SourceRef: sourceRef),
                Amount: amount,
                PostedDetail: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["sourceRef"] = sourceRef,
                    ["amount"] = amount,
                },
                RefusedDetail: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["sourceRef"] = sourceRef,
                }));
        }

        return plan;

        static PlannedExclusion Exclude(Guid leaseId, RunItemStatus status, string reason) =>
            new(RunTargetKind.Lease, leaseId, status,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["reason"] = reason });
    }

    // ── the decision ─────────────────────────────────────────────────────────

    /// <summary>
    /// What this run should do about one delinquent lease. A closed hierarchy of exactly two cases,
    /// so the cast in each caller is exhaustive in fact and not merely by convention (the same
    /// reasoning as <see cref="RunPlanItem"/>).
    /// </summary>
    private abstract record LateFeeDecision
    {
        private protected LateFeeDecision() { }
    }

    /// <summary>Charge this lease. Carries every derived fact either projection needs.</summary>
    private sealed record ChargeLateFee(
        Guid RentObligationEntryId,
        decimal Amount,
        DateOnly DueDate,
        DateOnly EligibilityDate,
        int DaysLate,
        LateFeeKind Kind) : LateFeeDecision;

    /// <summary>
    /// Do not charge this lease, and why — in <b>both</b> vocabularies at once.
    /// <para>
    /// <paramref name="Code"/> is the machine reason the run log records; <paramref name="Explanation"/>
    /// is the sentence the operator reads in the preview. They are constructed together, at the single
    /// point the condition is recognised, precisely because they had drifted into two parallel
    /// vocabularies stated in two different loops (#199).
    /// </para>
    /// </summary>
    private sealed record IneligibleForLateFee(string Code, string Explanation) : LateFeeDecision;

    /// <summary>
    /// The one statement of late-fee eligibility and amount, projected by <see cref="PreviewAsync"/>
    /// and by <see cref="PlanAsync"/>.
    /// <para>
    /// <b>Pure by construction.</b> It takes data and returns a decision — it fetches nothing. That is
    /// what lets both paths share the rules while each still reads its own data: confirm re-fetches
    /// delinquency and policy at confirmation time and hands them here, so a canonical decision never
    /// becomes a cached one (ADR-019).
    /// </para>
    /// <para>
    /// <b>Why it exists.</b> Preview and plan previously derived the NC §42-46 threshold, the
    /// eligibility date and the amount independently — the statutory clamp was written twice by hand,
    /// in the same commit (ADR-033, <c>69df023</c>). Deleting <c>Math.Max(5, …)</c> from the preview
    /// copy left the entire suite green, because every eligibility test drove the plan path. One
    /// statement means the plan-side tests now pin what the operator is shown as well.
    /// </para>
    /// </summary>
    private static LateFeeDecision Decide(
        DelinquentLedgerRow row,
        LateFeePolicy? policy,
        DateOnly assessmentDate,
        RunPeriod period)
    {
        var rentObligationEntryId = row.Attribution switch
        {
            DelinquencyAttribution.AttributedToLease attributed => attributed.RentObligationEntryId,
            DelinquencyAttribution.NoRentObligation => (Guid?)null,
            _ => throw new InvalidOperationException(
                $"Unknown delinquency attribution case '{row.Attribution.GetType().Name}'."),
        };

        if (rentObligationEntryId is null)
        {
            return new IneligibleForLateFee(
                "rent_obligation_not_found",
                $"{row.TenantName}: no open rent obligation found for {period.Key} — skipped.");
        }

        if (policy is null)
        {
            return new IneligibleForLateFee(
                "no_policy",
                $"{row.TenantName}: no late-fee policy found — skipped.");
        }

        var dueDate = new DateOnly(period.Year, period.Month, policy.RentDueDay);

        // NC §42-46: the day after the contractual due date is late day one, so due + 5 is the
        // earliest permitted assessment. A lease may extend that threshold and cannot shorten it —
        // which is what the clamp says, and why it must not be possible to state it on only one path.
        var contractualThreshold = Math.Max(5, policy.GraceDays);
        var eligibilityDate = dueDate.AddDays(contractualThreshold);
        var daysLate = assessmentDate.DayNumber - dueDate.DayNumber;

        if (assessmentDate < eligibilityDate)
        {
            return new IneligibleForLateFee(
                "before_late_fee_eligibility",
                $"{row.TenantName}: not eligible until {eligibilityDate:yyyy-MM-dd} " +
                $"({daysLate} late days, threshold {contractualThreshold}) — skipped.");
        }

        return new ChargeLateFee(
            RentObligationEntryId: rentObligationEntryId.Value,
            Amount: LateFeeCalculator.Compute(policy, row.Rent),
            DueDate: dueDate,
            EligibilityDate: eligibilityDate,
            DaysLate: daysLate,
            Kind: policy.Kind);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>ADR-033. One deterministic key per rent obligation.</summary>
    private static string SourceRef(Guid rentObligationEntryId) =>
        $"latefee:rent-entry={rentObligationEntryId}";

    private DateOnly CurrentDate() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
}
