using System.Text.Json;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// <see cref="IRunStrategy"/> for <see cref="RunType.Rent"/>. Previews and confirms the monthly
/// rent charge run, applying actual-days proration (ADR-017) for leases that start or end mid-month.
/// <para>
/// <b>Source-ref convention (ADR-019):</b> <c>rent:{year}-{month:00}:lease={leaseId}</c>.
/// The existing <c>(org_id, source_ref)</c> partial unique index on <c>journal_entries</c>
/// deduplicates repeat runs; a <c>DuplicateSourceRefException</c> on confirm is caught
/// per-item and recorded as <see cref="RunItemStatus.Skipped"/>.
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
/// A locked bank period (<c>AccountPeriodLockedException</c>) or a closed accounting period
/// (<c>PeriodClosedException</c>) is surfaced per-item during confirm (caught → <see cref="RunItemStatus.Excluded"/>).
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
    public async Task<IReadOnlyList<BulkRunItem>> ConfirmAsync(
        BulkRun run,
        IReadOnlyList<Guid> selectedTargetIds,
        IBatchPosting posting,
        RunCapabilities capabilities,
        CancellationToken ct)
    {
        // Frozen at RunEngine.ConfirmAsync entry and unused here: no capability gates this run type
        // yet. When one does, it may only decide whether a target is posted at all — never an amount,
        // a date, or any other input to the business event.
        _ = capabilities;

        var period = new RunPeriod(run.PeriodYear, run.PeriodMonth);
        var selectedSet = new HashSet<Guid>(selectedTargetIds);

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

        // Build intents for selected, eligible leases.
        var intents = new List<RentChargeIntent>(selectedTargetIds.Count);
        var skippedItems = new List<BulkRunItem>();

        var chargeDate = new DateOnly(period.Year, period.Month, 1);

        foreach (var leaseId in selectedTargetIds)
        {
            if (!byLeaseId.TryGetValue(leaseId, out var row))
            {
                // Target not found in schedule — record as excluded.
                skippedItems.Add(BulkRunItem.Create(
                    run.Id, RunTargetKind.Lease, leaseId,
                    RunItemStatus.Excluded, 0m,
                    JsonSerializer.Serialize(new { reason = "lease_not_in_schedule" }),
                    run.CreatedAt));
                continue;
            }

            if (row.Rent == 0m)
            {
                skippedItems.Add(BulkRunItem.Create(
                    run.Id, RunTargetKind.Lease, leaseId,
                    RunItemStatus.Excluded, 0m,
                    JsonSerializer.Serialize(new { reason = "rent_zero" }),
                    run.CreatedAt));
                continue;
            }

            // Structural cross-source guard: skip if any RentCharged already exists for this
            // tenant in the period, regardless of source_ref (manual, seed, import, or other run).
            if (alreadyChargedTenants.Contains(row.TenantId))
            {
                skippedItems.Add(BulkRunItem.Create(
                    run.Id, RunTargetKind.Lease, leaseId,
                    RunItemStatus.Skipped, 0m,
                    JsonSerializer.Serialize(new { reason = "already_charged_in_period" }),
                    run.CreatedAt));
                continue;
            }

            var amount = Proration.Charge(row.Rent, period.Year, period.Month, row.StartDate, row.EndDate);
            var prorated = amount != row.Rent;
            var description = prorated
                ? $"Rent {period.Key} — {row.TenantName} {row.UnitLabel} (prorated)"
                : $"Rent {period.Key} — {row.TenantName} {row.UnitLabel}";

            intents.Add(new RentChargeIntent(
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

        // Post all intents. Catch DuplicateSourceRefException per-item → Skipped.
        // Catch AccountPeriodLockedException per-item → Excluded (bank period locked).
        // Catch PeriodClosedException per-item → Excluded (accounting period closed).
        var items = new List<BulkRunItem>(selectedTargetIds.Count);
        items.AddRange(skippedItems);

        // One intent at a time, and every refusal comes back as an outcome rather than an exception,
        // so the loop records each and carries on. This used to be per-item for a worse reason: the
        // port's only failure channel was a throw, which would have aborted a whole batch.
        foreach (var intent in intents)
        {
            var outcome = await posting.PostAsync(intent, ct);

            items.Add(outcome.Status switch
            {
                PostStatus.Posted => BulkRunItem.Create(
                    run.Id, RunTargetKind.Lease, intent.LeaseId,
                    RunItemStatus.Posted, intent.Amount,
                    JsonSerializer.Serialize(new
                    {
                        entryId = outcome.EntryId,
                        sourceRef = intent.SourceRef,
                        amount = intent.Amount,
                        prorated = intent.Amount != byLeaseId[intent.LeaseId].Rent,
                    }),
                    run.CreatedAt,
                    resultingJournalEntryId: outcome.EntryId),

                PostStatus.DuplicateSourceRef => Refused(RunItemStatus.Skipped, "duplicate_source_ref"),
                PostStatus.PeriodLocked => Refused(RunItemStatus.Excluded, "period_locked"),
                PostStatus.PeriodClosed => Refused(RunItemStatus.Excluded, "period_closed"),

                // ReserveFloor is a disbursement-only refusal; reaching it here means the adapter
                // mis-routed an intent, which is a wiring bug rather than a per-item outcome.
                _ => throw new InvalidOperationException(
                    $"Unexpected posting outcome {outcome.Status} for a rent charge."),
            });

            BulkRunItem Refused(RunItemStatus status, string reason) => BulkRunItem.Create(
                run.Id, RunTargetKind.Lease, intent.LeaseId, status, 0m,
                JsonSerializer.Serialize(new { sourceRef = intent.SourceRef, reason }),
                run.CreatedAt);
        }

        return items;
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
