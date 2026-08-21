using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// <see cref="IRunStrategy"/> for <see cref="RunType.Rent"/>. Previews the monthly rent charge run and
/// plans it, applying actual-days proration (ADR-017) for leases that start or end mid-month.
/// <para>
/// <b>Source-ref convention (ADR-019):</b> <c>rent:{year}-{month:00}:lease={leaseId}</c>.
/// The existing <c>(org_id, source_ref)</c> partial unique index on <c>journal_entries</c>
/// deduplicates repeat runs; the engine records the resulting refusal as
/// <see cref="RunItemStatus.Skipped"/>.
/// </para>
/// <para>
/// <b>Charge date:</b> the first day of the period month (<c>new DateOnly(year, month, 1)</c>).
/// Proration affects the amount, not the date.
/// </para>
/// <para>
/// <b>Exceptions (surfaces in preview, not as rows):</b>
/// <list type="bullet">
///   <item>Lease with <see cref="LeaseScheduleRow.Rent"/> == 0 (not chargeable).</item>
///   <item>Lease ended before the period (term does not overlap).</item>
/// </list>
/// A locked bank period or a closed accounting period comes back from the posting port as a refusal
/// and the engine records it per-item as <see cref="RunItemStatus.Excluded"/>; neither is visible
/// here.
/// </para>
/// </summary>
public sealed class RentRunStrategy(
    ILeaseScheduleData schedule,
    IPostedSourceRefs postedRefs,
    IPeriodChargeGuard periodGuard) : IRunStrategy
{
    /// <inheritdoc />
    public RunType RunType => RunType.Rent;

    /// <inheritdoc />
    public async Task<StrategyPreview> PreviewAsync(RunPeriod period, CancellationToken ct)
    {
        var rows = await schedule.GetActiveAsync(period.Year, period.Month, ct);

        // Build candidate source_ref keys for the same-source idempotency pre-check.
        var allKeys = rows
            .Select(r => SourceRef(period, r.LeaseId))
            .ToList();

        var alreadyPosted = await BatchRead.SetOrEmptyAsync(
            allKeys, keys => postedRefs.GetExistingAsync(keys, ct));

        // Structural cross-source period guard: detect RentCharged entries posted by any means
        // (manual composer, seed, import) so we never double-charge a tenant in a period.
        var allTenantIds = rows.Select(r => r.TenantId).ToList();
        var alreadyChargedTenants = await BatchRead.SetOrEmptyAsync(
            allTenantIds,
            ids => periodGuard.GetChargedTenantsAsync(
                "RentCharged", null, period.Year, period.Month, ids, ct));

        var previewRows = new List<PreviewRow>(rows.Count);
        var exceptions = new List<string>();

        foreach (var row in rows)
        {
            var decision = Decide(row, period);
            if (decision is IneligibleForRent ineligible)
            {
                exceptions.Add(ineligible.Explanation);
                continue;
            }

            var charge = (ChargeRent)decision;

            // Preview's already-done test is deliberately wider than plan's: it reports the
            // source_ref hit as well as the cross-source guard, so the operator sees "this will be
            // skipped" before selecting. Plan leaves the source_ref half to the unique index and
            // records the refusal the engine maps. Both converge on Skipped; only the display is
            // eager, which is why this is not part of the shared decision.
            var alreadyDone = alreadyPosted.Contains(SourceRef(period, row.LeaseId))
                || alreadyChargedTenants.Contains(row.TenantId);

            var detail = new Dictionary<string, string>
            {
                ["unit"] = row.UnitLabel,
                ["monthlyRent"] = row.Rent.ToString("F2"),
            };
            if (charge.Prorated)
            {
                detail["prorated"] = "true";
                detail["proratedAmount"] = charge.Amount.ToString("F2");
            }

            previewRows.Add(new PreviewRow(
                TargetKind: RunTargetKind.Lease,
                TargetId: row.LeaseId,
                Label: row.TenantName,
                Amount: charge.Amount,
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
        // Re-fetch the schedule to get current data (preview may be stale).
        var allRows = await schedule.GetActiveAsync(period.Year, period.Month, ct);
        var byLeaseId = allRows.ToDictionary(r => r.LeaseId);

        // Re-run the structural cross-source period guard at confirmation time (prevents double-charge
        // even when a manual charge was posted between preview and run confirmation).
        var tenantIdsInScope = selectedTargetIds
            .Where(id => byLeaseId.ContainsKey(id))
            .Select(id => byLeaseId[id].TenantId)
            .Distinct()
            .ToList();
        var alreadyChargedTenants = await BatchRead.SetOrEmptyAsync(
            tenantIdsInScope,
            ids => periodGuard.GetChargedTenantsAsync(
                "RentCharged", null, period.Year, period.Month, ids, ct));

        var plan = new List<RunPlanItem>(selectedTargetIds.Count);
        var chargeDate = new DateOnly(period.Year, period.Month, 1);

        foreach (var leaseId in selectedTargetIds)
        {
            if (!byLeaseId.TryGetValue(leaseId, out var row))
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Excluded, "lease_not_in_schedule"));
                continue;
            }

            // The same decision the preview projected, re-derived from confirmation-time data.
            // Evaluated before the already-charged guard so a zero-rent lease that was also charged
            // by another route still records rent_zero, exactly as it did before.
            var decision = Decide(row, period);
            if (decision is IneligibleForRent ineligible)
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Excluded, ineligible.Code));
                continue;
            }

            // Structural cross-source guard: skip if any RentCharged already exists for this
            // tenant in the period, regardless of source_ref (manual, seed, import, or other run).
            if (alreadyChargedTenants.Contains(row.TenantId))
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Skipped, "already_charged_in_period"));
                continue;
            }

            var charge = (ChargeRent)decision;
            var amount = charge.Amount;
            var prorated = charge.Prorated;
            var description = prorated
                ? $"Rent {period.Key} — {row.TenantName} {row.UnitLabel} (prorated)"
                : $"Rent {period.Key} — {row.TenantName} {row.UnitLabel}";
            var sourceRef = SourceRef(period, leaseId);
            var dueDate = new DateOnly(period.Year, period.Month, row.RentDueDay);

            plan.Add(new PlannedPosting(
                TargetKind: RunTargetKind.Lease,
                TargetId: leaseId,
                Intent: new RentChargeIntent(
                    LeaseId: leaseId,
                    TenantId: row.TenantId,
                    PropertyId: row.PropertyId,
                    OwnerId: row.OwnerId,
                    UnitId: row.UnitId,
                    Amount: amount,
                    Date: chargeDate,
                    DueDate: dueDate,
                    Description: description,
                    SourceRef: sourceRef),
                Amount: amount,
                PostedDetail: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["sourceRef"] = sourceRef,
                    ["amount"] = amount,
                    ["prorated"] = prorated,
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
    /// What this run should do about one scheduled lease. Closed hierarchy of exactly two cases, so
    /// each caller's cast is exhaustive in fact (the same reasoning as <see cref="RunPlanItem"/>).
    /// </summary>
    private abstract record RentDecision
    {
        private protected RentDecision() { }
    }

    /// <summary>Charge this lease, at the prorated amount when its term does not span the month.</summary>
    private sealed record ChargeRent(decimal Amount, bool Prorated) : RentDecision;

    /// <summary>
    /// Do not charge this lease, and why — in <b>both</b> vocabularies at once. <paramref name="Code"/>
    /// is what the run log records; <paramref name="Explanation"/> is what the operator reads in the
    /// preview. Built together so the two cannot drift apart (#199).
    /// </summary>
    private sealed record IneligibleForRent(string Code, string Explanation) : RentDecision;

    /// <summary>
    /// The one statement of rent chargeability and amount, projected by <see cref="PreviewAsync"/>
    /// and by <see cref="PlanAsync"/>.
    /// <para>
    /// Pure by construction: it takes a schedule row and returns a decision, fetching nothing. Both
    /// paths still read their own data — confirm re-fetches the schedule and hands it here — so the
    /// canonical decision never becomes a cached one (ADR-019).
    /// </para>
    /// <para>
    /// The zero-rent test previously lived in both loops, and deleting the preview copy left the whole
    /// suite green: every test of the rule drove the plan path. It is now stated once.
    /// </para>
    /// </summary>
    private static RentDecision Decide(LeaseScheduleRow row, RunPeriod period)
    {
        if (row.Rent == 0m)
        {
            return new IneligibleForRent("rent_zero", $"{row.TenantName}: rent is 0 — skipped.");
        }

        var amount = Proration.Charge(row.Rent, period.Year, period.Month, row.StartDate, row.EndDate);
        return new ChargeRent(amount, Prorated: amount != row.Rent);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// ADR-019 §2. Deliberately built here rather than centrally: the ADR's generalised shape
    /// <c>{runType}:{year}-{month:00}:{target}</c> does not describe its own table — <c>latefee</c> is
    /// not <c>LateFee</c> lowercased and <c>disbursement-fee</c> is not a run type at all — so a
    /// helper deriving the prefix from <c>RunType</c> would change two of the four keys and break
    /// idempotency against already-committed runs, silently, as duplicate postings.
    /// </summary>
    private static string SourceRef(RunPeriod period, Guid leaseId) =>
        $"rent:{period.Key}:lease={leaseId}";
}
