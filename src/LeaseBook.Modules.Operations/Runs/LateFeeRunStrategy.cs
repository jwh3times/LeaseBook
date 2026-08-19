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
        var alreadyPosted = allKeys.Count > 0
            ? await postedRefs.GetExistingAsync(allKeys, ct)
            : (IReadOnlySet<string>)new HashSet<string>();

        var previewRows = new List<PreviewRow>(delinquentRows.Count);
        var exceptions = new List<string>();

        foreach (var row in delinquentRows)
        {
            Guid rentObligationEntryId;
            switch (row.Attribution)
            {
                case DelinquencyAttribution.AttributedToLease attributed:
                    rentObligationEntryId = attributed.RentObligationEntryId;
                    break;
                case DelinquencyAttribution.NoRentObligation:
                    exceptions.Add($"{row.TenantName}: no open rent obligation found for {period.Key} — skipped.");
                    continue;
                default:
                    throw new InvalidOperationException(
                        $"Unknown delinquency attribution case '{row.Attribution.GetType().Name}'.");
            }

            if (!policyMap.TryGetValue(row.LeaseId, out var policy))
            {
                exceptions.Add($"{row.TenantName}: no late-fee policy found — skipped.");
                continue;
            }

            var dueDate = new DateOnly(period.Year, period.Month, policy.RentDueDay);
            var contractualThreshold = Math.Max(5, policy.GraceDays);
            var eligibilityDate = dueDate.AddDays(contractualThreshold);
            var daysLate = assessmentDate.DayNumber - dueDate.DayNumber;

            if (assessmentDate < eligibilityDate)
            {
                exceptions.Add($"{row.TenantName}: not eligible until {eligibilityDate:yyyy-MM-dd} " +
                    $"({daysLate} late days, threshold {contractualThreshold}) — skipped.");
                continue;
            }

            var amount = LateFeeCalculator.Compute(policy, row.Rent);
            var key = SourceRef(rentObligationEntryId);
            var alreadyDone = alreadyPosted.Contains(key);

            var detail = new Dictionary<string, string>
            {
                ["unit"] = row.UnitLabel,
                ["balance"] = row.Balance.ToString("F2"),
                ["daysLate"] = daysLate.ToString(),
                ["dueDate"] = dueDate.ToString("yyyy-MM-dd"),
                ["assessmentDate"] = assessmentDate.ToString("yyyy-MM-dd"),
                ["eligibilityDate"] = eligibilityDate.ToString("yyyy-MM-dd"),
                ["feeKind"] = policy.Kind.ToString(),
                ["monthlyRent"] = row.Rent.ToString("F2"),
            };

            previewRows.Add(new PreviewRow(
                TargetKind: RunTargetKind.Lease,
                TargetId: row.LeaseId,
                Label: row.TenantName,
                Amount: amount,
                AlreadyDone: alreadyDone,
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
        var policyMap = selectedInSchedule.Count > 0
            ? await policies.GetAsync(selectedInSchedule, ct)
            : (IReadOnlyDictionary<Guid, LateFeePolicy>)new Dictionary<Guid, LateFeePolicy>();

        var plan = new List<RunPlanItem>(selectedTargetIds.Count);

        foreach (var leaseId in selectedTargetIds)
        {
            if (!byLeaseId.TryGetValue(leaseId, out var row))
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Excluded, "lease_not_delinquent"));
                continue;
            }

            Guid rentObligationEntryId;
            switch (row.Attribution)
            {
                case DelinquencyAttribution.AttributedToLease attributed:
                    rentObligationEntryId = attributed.RentObligationEntryId;
                    break;
                case DelinquencyAttribution.NoRentObligation:
                    plan.Add(Exclude(leaseId, RunItemStatus.Excluded, "rent_obligation_not_found"));
                    continue;
                default:
                    throw new InvalidOperationException(
                        $"Unknown delinquency attribution case '{row.Attribution.GetType().Name}'.");
            }

            if (!policyMap.TryGetValue(leaseId, out var policy))
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Excluded, "no_policy"));
                continue;
            }

            var dueDate = new DateOnly(period.Year, period.Month, policy.RentDueDay);
            var contractualThreshold = Math.Max(5, policy.GraceDays);
            var eligibilityDate = dueDate.AddDays(contractualThreshold);

            // Eligibility is re-established at confirmation from the contractual due date. The day
            // after due is late day one, so due + 5 is the first statutory charge date; a lease may
            // extend that threshold but cannot shorten it.
            if (assessmentDate < eligibilityDate)
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Excluded, "before_late_fee_eligibility"));
                continue;
            }

            var amount = LateFeeCalculator.Compute(policy, row.Rent);
            var description = $"Late fee {period.Key} — {row.TenantName} {row.UnitLabel}";
            var sourceRef = SourceRef(rentObligationEntryId);

            plan.Add(new PlannedPosting(
                TargetKind: RunTargetKind.Lease,
                TargetId: leaseId,
                Intent: new LateFeeIntent(
                    LeaseId: leaseId,
                    RentObligationEntryId: rentObligationEntryId,
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

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>ADR-033. One deterministic key per rent obligation.</summary>
    private static string SourceRef(Guid rentObligationEntryId) =>
        $"latefee:rent-entry={rentObligationEntryId}";

    private DateOnly CurrentDate() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
}
