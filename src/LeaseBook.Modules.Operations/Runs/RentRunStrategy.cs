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
    public async Task<RunPreview> PreviewAsync(RunPeriod period, CancellationToken ct)
    {
        var rows = await schedule.GetActiveAsync(period.Year, period.Month, ct);

        // Build candidate source_ref keys for the same-source idempotency pre-check.
        var allKeys = rows
            .Select(r => SourceRef(period, r.LeaseId))
            .ToList();

        var alreadyPosted = allKeys.Count > 0
            ? await postedRefs.GetExistingAsync(allKeys, ct)
            : (IReadOnlySet<string>)new HashSet<string>();

        // Structural cross-source period guard: detect RentCharged entries posted by any means
        // (manual composer, seed, import) so we never double-charge a tenant in a period.
        var allTenantIds = rows.Select(r => r.TenantId).ToList();
        var alreadyChargedTenants = allTenantIds.Count > 0
            ? await periodGuard.GetChargedTenantsAsync("RentCharged", null, period.Year, period.Month, allTenantIds, ct)
            : (IReadOnlySet<Guid>)new HashSet<Guid>();

        var previewRows = new List<PreviewRow>(rows.Count);
        var exceptions = new List<string>();

        foreach (var row in rows)
        {
            // Exception: no rent set.
            if (row.Rent == 0m)
            {
                exceptions.Add($"{row.TenantName}: rent is 0 — skipped.");
                continue;
            }

            var amount = Proration.Charge(row.Rent, period.Year, period.Month, row.StartDate, row.EndDate);
            var prorated = amount != row.Rent;
            var key = SourceRef(period, row.LeaseId);
            var alreadyDone = alreadyPosted.Contains(key) || alreadyChargedTenants.Contains(row.TenantId);

            var detail = new Dictionary<string, string>
            {
                ["unit"] = row.UnitLabel,
                ["monthlyRent"] = row.Rent.ToString("F2"),
            };
            if (prorated)
            {
                detail["prorated"] = "true";
                detail["proratedAmount"] = amount.ToString("F2");
            }

            previewRows.Add(new PreviewRow(
                TargetKind: RunTargetKind.Lease,
                TargetId: row.LeaseId,
                Label: row.TenantName,
                Amount: amount,
                AlreadyDone: alreadyDone,
                ExcludedReason: null,
                Detail: detail));
        }

        return new RunPreview(RunType.Rent, period, previewRows, exceptions);
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

        // Re-run the structural cross-source period guard at confirm time (prevents double-charge
        // even when a manual charge was posted between preview and confirm).
        var tenantIdsInScope = selectedTargetIds
            .Where(id => byLeaseId.ContainsKey(id))
            .Select(id => byLeaseId[id].TenantId)
            .Distinct()
            .ToList();
        var alreadyChargedTenants = tenantIdsInScope.Count > 0
            ? await periodGuard.GetChargedTenantsAsync("RentCharged", null, period.Year, period.Month, tenantIdsInScope, ct)
            : (IReadOnlySet<Guid>)new HashSet<Guid>();

        var plan = new List<RunPlanItem>(selectedTargetIds.Count);
        var chargeDate = new DateOnly(period.Year, period.Month, 1);

        foreach (var leaseId in selectedTargetIds)
        {
            if (!byLeaseId.TryGetValue(leaseId, out var row))
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Excluded, "lease_not_in_schedule"));
                continue;
            }

            if (row.Rent == 0m)
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Excluded, "rent_zero"));
                continue;
            }

            // Structural cross-source guard: skip if any RentCharged already exists for this
            // tenant in the period, regardless of source_ref (manual, seed, import, or other run).
            if (alreadyChargedTenants.Contains(row.TenantId))
            {
                plan.Add(Exclude(leaseId, RunItemStatus.Skipped, "already_charged_in_period"));
                continue;
            }

            var amount = Proration.Charge(row.Rent, period.Year, period.Month, row.StartDate, row.EndDate);
            var prorated = amount != row.Rent;
            var description = prorated
                ? $"Rent {period.Key} — {row.TenantName} {row.UnitLabel} (prorated)"
                : $"Rent {period.Key} — {row.TenantName} {row.UnitLabel}";
            var sourceRef = SourceRef(period, leaseId);

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
