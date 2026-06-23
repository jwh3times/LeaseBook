using System.Text.Json;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// <see cref="IRunStrategy"/> for <see cref="RunType.LateFee"/>. Previews and confirms the monthly
/// late-fee charge run, applying the NC §42-46 statutory clamp via <see cref="LateFeeCalculator"/>.
/// <para>
/// <b>Source-ref convention (ADR-019):</b> <c>latefee:{year}-{month:00}:lease={leaseId}</c>.
/// The existing <c>(org_id, source_ref)</c> partial unique index on <c>journal_entries</c>
/// deduplicates repeat runs; a <c>DuplicateSourceRefException</c> on confirm is caught per-item
/// and recorded as <see cref="RunItemStatus.Skipped"/>.
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
/// <b>Exceptions (surfaces in preview, not as rows):</b>
/// <list type="bullet">
///   <item>Lease with <see cref="DelinquentLedgerRow.Balance"/> == 0 or within grace period.</item>
///   <item>Lease with <see cref="DelinquentLedgerRow.DaysLate"/> == -1 (tenant has multiple active
///     leases; balance cannot be attributed — excluded as <c>ambiguous_multiple_active_leases</c>).</item>
///   <item>Lease with no effective policy resolved.</item>
/// </list>
/// A locked bank period (<c>AccountPeriodLockedException</c>) or a closed accounting period
/// (<c>PeriodClosedException</c>) is surfaced per-item during confirm (caught →
/// <see cref="RunItemStatus.Excluded"/>).
/// </para>
/// </summary>
public sealed class LateFeeRunStrategy(
    IDelinquencyData delinquency,
    ILateFeePolicyData policies,
    IPostedSourceRefs postedRefs) : IRunStrategy
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

        var previewRows = new List<PreviewRow>(delinquentRows.Count);
        var exceptions = new List<string>();

        foreach (var row in delinquentRows)
        {
            // DaysLate == -1 is the sentinel set by the adapter when the tenant has more than one
            // active lease and the balance cannot be attributed to a single lease.
            if (row.DaysLate < 0)
            {
                exceptions.Add($"Lease {row.LeaseId} ({row.TenantName}): ambiguous_multiple_active_leases — tenant has multiple active leases; cannot attribute balance. Skipped.");
                continue;
            }

            if (!policyMap.TryGetValue(row.LeaseId, out var policy))
            {
                exceptions.Add($"Lease {row.LeaseId} ({row.TenantName}): no late-fee policy found — skipped.");
                continue;
            }

            // Gate: a lease is eligible when its oldest past-due charge is strictly past the grace
            // window (DaysLate > GraceDays). A charge exactly GraceDays old is still within grace.
            // DaysLate is the real age in days from GetDelinquencyAging.OldestAgeDays, not a bucket floor.
            if (row.DaysLate <= policy.GraceDays)
            {
                exceptions.Add($"Lease {row.LeaseId} ({row.TenantName}): within grace period ({row.DaysLate} days late, {policy.GraceDays} grace) — skipped.");
                continue;
            }

            var amount = LateFeeCalculator.Compute(policy, row.Rent);
            var key = SourceRef(period, row.LeaseId);
            var alreadyDone = alreadyPosted.Contains(key);

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
    public async Task<IReadOnlyList<BulkRunItem>> ConfirmAsync(
        BulkRun run,
        IReadOnlyList<Guid> selectedTargetIds,
        IBatchPosting posting,
        CancellationToken ct)
    {
        var period = new RunPeriod(run.PeriodYear, run.PeriodMonth);
        var selectedSet = new HashSet<Guid>(selectedTargetIds);

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

        var chargeDate = new DateOnly(period.Year, period.Month, 1);

        // Build intents for selected, eligible leases.
        var intents = new List<LateFeeIntent>(selectedTargetIds.Count);
        var skippedItems = new List<BulkRunItem>();

        foreach (var leaseId in selectedTargetIds)
        {
            if (!byLeaseId.TryGetValue(leaseId, out var row))
            {
                skippedItems.Add(BulkRunItem.Create(
                    run.Id, RunTargetKind.Lease, leaseId,
                    RunItemStatus.Excluded, 0m,
                    JsonSerializer.Serialize(new { reason = "lease_not_delinquent" }),
                    run.CreatedAt));
                continue;
            }

            if (!policyMap.TryGetValue(leaseId, out var policy))
            {
                skippedItems.Add(BulkRunItem.Create(
                    run.Id, RunTargetKind.Lease, leaseId,
                    RunItemStatus.Excluded, 0m,
                    JsonSerializer.Serialize(new { reason = "no_policy" }),
                    run.CreatedAt));
                continue;
            }

            var amount = LateFeeCalculator.Compute(policy, row.Rent);
            var description = $"Late fee {period.Key} — {row.TenantName} {row.UnitLabel}";

            intents.Add(new LateFeeIntent(
                LeaseId: leaseId,
                TenantId: row.TenantId,
                PropertyId: row.PropertyId,
                OwnerId: row.OwnerId,
                UnitId: row.UnitId,
                Amount: amount,
                Date: chargeDate,
                Description: description,
                SourceRef: SourceRef(period, leaseId)));
        }

        // Post one at a time so we can catch per-item exceptions cleanly.
        // Catch DuplicateSourceRefException per-item → Skipped (idempotent re-run).
        // Catch AccountPeriodLockedException per-item → Excluded (bank period locked).
        // Catch PeriodClosedException per-item → Excluded (accounting period closed).
        var items = new List<BulkRunItem>(selectedTargetIds.Count);
        items.AddRange(skippedItems);

        foreach (var intent in intents)
        {
            BulkRunItem item;
            try
            {
                var resultMap = await posting.PostLateFeesAsync([intent], ct);
                var entryId = resultMap[intent.LeaseId];
                var snapshot = JsonSerializer.Serialize(new
                {
                    entryId,
                    sourceRef = intent.SourceRef,
                    amount = intent.Amount,
                });
                item = BulkRunItem.Create(
                    run.Id, RunTargetKind.Lease, intent.LeaseId,
                    RunItemStatus.Posted, intent.Amount, snapshot, run.CreatedAt);
            }
            catch (Exception ex) when (IsDuplicateSourceRef(ex))
            {
                var snapshot = JsonSerializer.Serialize(new
                {
                    sourceRef = intent.SourceRef,
                    reason = "duplicate_source_ref",
                });
                item = BulkRunItem.Create(
                    run.Id, RunTargetKind.Lease, intent.LeaseId,
                    RunItemStatus.Skipped, 0m, snapshot, run.CreatedAt);
            }
            catch (Exception ex) when (IsPeriodLocked(ex))
            {
                var snapshot = JsonSerializer.Serialize(new
                {
                    sourceRef = intent.SourceRef,
                    reason = "period_locked",
                });
                item = BulkRunItem.Create(
                    run.Id, RunTargetKind.Lease, intent.LeaseId,
                    RunItemStatus.Excluded, 0m, snapshot, run.CreatedAt);
            }
            catch (Exception ex) when (IsPeriodClosed(ex))
            {
                var snapshot = JsonSerializer.Serialize(new
                {
                    sourceRef = intent.SourceRef,
                    reason = "period_closed",
                });
                item = BulkRunItem.Create(
                    run.Id, RunTargetKind.Lease, intent.LeaseId,
                    RunItemStatus.Excluded, 0m, snapshot, run.CreatedAt);
            }
            items.Add(item);
        }

        return items;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string SourceRef(RunPeriod period, Guid leaseId) =>
        $"latefee:{period.Key}:lease={leaseId}";

    /// <summary>
    /// Checks for DuplicateSourceRefException without referencing Accounting assembly
    /// (ADR-007: Operations references SharedKernel only — no Accounting types).
    /// </summary>
    private static bool IsDuplicateSourceRef(Exception ex) =>
        ex.GetType().Name == "DuplicateSourceRefException";

    /// <summary>Checks for AccountPeriodLockedException without referencing Accounting assembly (ADR-007).</summary>
    private static bool IsPeriodLocked(Exception ex) =>
        ex.GetType().Name == "AccountPeriodLockedException";

    /// <summary>
    /// Checks for PeriodClosedException without referencing Accounting assembly (ADR-007).
    /// A FeeCharged posting into a closed accounting period raises this; the item is Excluded.
    /// </summary>
    private static bool IsPeriodClosed(Exception ex) =>
        ex.GetType().Name == "PeriodClosedException";
}
