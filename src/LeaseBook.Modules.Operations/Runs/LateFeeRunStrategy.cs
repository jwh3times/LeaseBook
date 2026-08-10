using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// <see cref="IRunStrategy"/> for <see cref="RunType.LateFee"/>. Previews the monthly late-fee charge
/// run and plans it, applying the NC §42-46 statutory clamp via <see cref="LateFeeCalculator"/>.
/// <para>
/// <b>Source-ref convention (ADR-019):</b> <c>latefee:{year}-{month:00}:lease={leaseId}</c>.
/// The existing <c>(org_id, source_ref)</c> partial unique index on <c>journal_entries</c>
/// deduplicates repeat runs; the engine records the resulting refusal as
/// <see cref="RunItemStatus.Skipped"/>.
/// </para>
/// <para>
/// <b>Charge date:</b> the first day of the period month (<c>new DateOnly(year, month, 1)</c>).
/// </para>
/// <para>
/// <b>Delinquency signal:</b> the <see cref="IDelinquencyData"/> port provides per-lease
/// receivable balances (from Accounting via the host adapter). Rent is always charged on the
/// period's 1st by the rent-charge run (WP-2); <see cref="DelinquentLedgerRow.DaysLate"/> is
/// the ACTUAL age in days of the oldest past-due charge (sourced from
/// <c>GetDelinquencyAging.OldestAgeDays</c>). A lease is eligible when
/// <c>DaysLate &gt; GraceDays</c> (strictly past the grace window; a charge exactly
/// <c>GraceDays</c> old is still within grace). The effective grace is resolved per-lease
/// from the effective policy (<see cref="ILateFeePolicyData"/>).
/// </para>
/// <para>
/// <b>Eligibility exclusions:</b> preview surfaces these as exceptions rather than chargeable rows;
/// planning re-establishes the same rules from its confirm-time data and records a selected target as
/// <see cref="RunItemStatus.Excluded"/> rather than posting it.
/// <list type="bullet">
///   <item>Lease with <see cref="DelinquentLedgerRow.Balance"/> == 0 or within grace period.</item>
///   <item>Lease with <see cref="DelinquentLedgerRow.DaysLate"/> == -1 (tenant has multiple active
///     leases; balance cannot be attributed — excluded as <c>ambiguous_multiple_active_leases</c>).</item>
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
    IPeriodChargeGuard periodGuard) : IRunStrategy
{
    /// <inheritdoc />
    public RunType RunType => RunType.LateFee;

    /// <inheritdoc />
    public async Task<RunPreview> PreviewAsync(RunPeriod period, CancellationToken ct)
    {
        // The "as of" date for aging is the last day of the period month. This ensures rent charges
        // posted on the 1st of the period have a positive age_days value by end-of-month (they land
        // in D1_30 after 1+ days), making them visible in GetDelinquencyAging. Running the late-fee
        // assessment end-of-month is the standard PM workflow: confirm who still owes before closing
        // the period.
        var asOf = new DateOnly(period.Year, period.Month, DateTime.DaysInMonth(period.Year, period.Month));

        // Fetch delinquent leases and their effective policies in parallel.
        var delinquentRows = await delinquency.GetAsync(period.Year, period.Month, asOf, ct);

        if (delinquentRows.Count == 0)
        {
            return new RunPreview(RunType.LateFee, period, [], []);
        }

        var leaseIds = delinquentRows.Select(r => r.LeaseId).ToList();

        // Fetch effective policies and already-posted source refs in parallel.
        var policyMap = await policies.GetAsync(leaseIds, ct);

        var allKeys = leaseIds.Select(id => SourceRef(period, id)).ToList();
        var alreadyPosted = allKeys.Count > 0
            ? await postedRefs.GetExistingAsync(allKeys, ct)
            : (IReadOnlySet<string>)new HashSet<string>();

        // Structural cross-source guard: detect FeeCharged/late entries posted by any means
        // so we never double-assess a late fee for the same tenant+period.
        var allTenantIds = delinquentRows.Select(r => r.TenantId).ToList();
        var alreadyChargedTenants = allTenantIds.Count > 0
            ? await periodGuard.GetChargedTenantsAsync("FeeCharged", "late", period.Year, period.Month, allTenantIds, ct)
            : (IReadOnlySet<Guid>)new HashSet<Guid>();

        var previewRows = new List<PreviewRow>(delinquentRows.Count);
        var exceptions = new List<string>();

        foreach (var row in delinquentRows)
        {
            // DaysLate == -1 is the sentinel set by the adapter when the tenant has more than one
            // active lease and the balance cannot be attributed to a single lease.
            if (row.DaysLate < 0)
            {
                exceptions.Add($"{row.TenantName}: multiple active leases — the balance cannot be attributed. Skipped.");
                continue;
            }

            if (!policyMap.TryGetValue(row.LeaseId, out var policy))
            {
                exceptions.Add($"{row.TenantName}: no late-fee policy found — skipped.");
                continue;
            }

            // Gate: a lease is eligible when its oldest past-due charge is strictly past the grace
            // window (DaysLate > GraceDays). A charge exactly GraceDays old is still within grace.
            // DaysLate is the real age in days from GetDelinquencyAging.OldestAgeDays, not a bucket floor.
            if (row.DaysLate <= policy.GraceDays)
            {
                exceptions.Add($"{row.TenantName}: within the grace period ({row.DaysLate} days late, {policy.GraceDays} allowed) — skipped.");
                continue;
            }

            var amount = LateFeeCalculator.Compute(policy, row.Rent);
            var key = SourceRef(period, row.LeaseId);
            var alreadyDone = alreadyPosted.Contains(key) || alreadyChargedTenants.Contains(row.TenantId);

            var detail = new Dictionary<string, string>
            {
                ["unit"] = row.UnitLabel,
                ["balance"] = row.Balance.ToString("F2"),
                ["daysLate"] = row.DaysLate.ToString(),
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

        return new RunPreview(RunType.LateFee, period, previewRows, exceptions);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunPlanItem>> PlanAsync(
        RunPeriod period,
        IReadOnlyList<Guid> selectedTargetIds,
        CancellationToken ct)
    {
        // Re-fetch delinquency data (preview may be stale). Use last day of period for same reason as PreviewAsync.
        var asOf = new DateOnly(period.Year, period.Month, DateTime.DaysInMonth(period.Year, period.Month));
        var allDelinquent = await delinquency.GetAsync(period.Year, period.Month, asOf, ct);
        var byLeaseId = allDelinquent.ToDictionary(r => r.LeaseId);

        // Fetch effective policies for selected leases.
        var selectedInSchedule = selectedTargetIds
            .Where(id => byLeaseId.ContainsKey(id))
            .ToList();
        var policyMap = selectedInSchedule.Count > 0
            ? await policies.GetAsync(selectedInSchedule, ct)
            : (IReadOnlyDictionary<Guid, LateFeePolicy>)new Dictionary<Guid, LateFeePolicy>();

        // Re-run the structural cross-source guard at confirm time.
        var tenantIdsInScope = selectedTargetIds
            .Where(id => byLeaseId.ContainsKey(id))
            .Select(id => byLeaseId[id].TenantId)
            .Distinct()
            .ToList();
        var alreadyChargedTenants = tenantIdsInScope.Count > 0
            ? await periodGuard.GetChargedTenantsAsync("FeeCharged", "late", period.Year, period.Month, tenantIdsInScope, ct)
            : (IReadOnlySet<Guid>)new HashSet<Guid>();

        var chargeDate = new DateOnly(period.Year, period.Month, 1);
        var plan = new List<RunPlanItem>(selectedTargetIds.Count);

        foreach (var leaseId in selectedTargetIds)
        {
            if (!byLeaseId.TryGetValue(leaseId, out var row))
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Excluded, "lease_not_delinquent"));
                continue;
            }

            if (row.DaysLate < 0)
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Excluded, "ambiguous_multiple_active_leases"));
                continue;
            }

            if (!policyMap.TryGetValue(leaseId, out var policy))
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Excluded, "no_policy"));
                continue;
            }

            // Confirm accepts target ids rather than a server-held preview token, so eligibility
            // must be re-established from the confirm-time rows. A target selected directly (or a
            // preview that became stale) must not bypass the statutory grace boundary.
            if (row.DaysLate <= policy.GraceDays)
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Excluded, "within_grace_period"));
                continue;
            }

            // Structural cross-source guard: skip if any FeeCharged/late already exists for this
            // tenant in the period, regardless of source_ref.
            if (alreadyChargedTenants.Contains(row.TenantId))
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Skipped, "already_charged_in_period"));
                continue;
            }

            var amount = LateFeeCalculator.Compute(policy, row.Rent);
            var description = $"Late fee {period.Key} — {row.TenantName} {row.UnitLabel}";
            var sourceRef = SourceRef(period, leaseId);

            plan.Add(new PlannedPosting(
                TargetKind: RunTargetKind.Lease,
                TargetId: leaseId,
                Intent: new LateFeeIntent(
                    LeaseId: leaseId,
                    TenantId: row.TenantId,
                    PropertyId: row.PropertyId,
                    OwnerId: row.OwnerId,
                    UnitId: row.UnitId,
                    Amount: amount,
                    Date: chargeDate,
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

    /// <summary>ADR-019 §2. Built here, not derived — see the note on <c>RentRunStrategy.SourceRef</c>.</summary>
    private static string SourceRef(RunPeriod period, Guid leaseId) =>
        $"latefee:{period.Key}:lease={leaseId}";
}
