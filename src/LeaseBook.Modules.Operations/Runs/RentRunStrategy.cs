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
/// A locked period is surfaced per-item during confirm (caught → <see cref="RunItemStatus.Excluded"/>).
/// </para>
/// </summary>
public sealed class RentRunStrategy(
    ILeaseScheduleData schedule,
    IPostedSourceRefs postedRefs) : IRunStrategy
{
    /// <inheritdoc />
    public RunType RunType => RunType.Rent;

    /// <inheritdoc />
    public async Task<RunPreview> PreviewAsync(RunPeriod period, CancellationToken ct)
    {
        var rows = await schedule.GetActiveAsync(period.Year, period.Month, ct);

        // Build candidate source_ref keys for the batch pre-check.
        var allKeys = rows
            .Select(r => SourceRef(period, r.LeaseId))
            .ToList();

        var alreadyPosted = allKeys.Count > 0
            ? await postedRefs.GetExistingAsync(allKeys, ct)
            : (IReadOnlySet<string>)new HashSet<string>();

        var previewRows = new List<PreviewRow>(rows.Count);
        var exceptions = new List<string>();

        foreach (var row in rows)
        {
            // Exception: no rent set.
            if (row.Rent == 0m)
            {
                exceptions.Add($"Lease {row.LeaseId} ({row.TenantName}): rent is 0 — skipped.");
                continue;
            }

            var amount = Proration.Charge(row.Rent, period.Year, period.Month, row.StartDate, row.EndDate);
            var prorated = amount != row.Rent;
            var key = SourceRef(period, row.LeaseId);
            var alreadyDone = alreadyPosted.Contains(key);

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
        CancellationToken ct)
    {
        var period = new RunPeriod(run.PeriodYear, run.PeriodMonth);
        var selectedSet = new HashSet<Guid>(selectedTargetIds);

        // Re-fetch the schedule to get current data (preview may be stale).
        var allRows = await schedule.GetActiveAsync(period.Year, period.Month, ct);
        var byLeaseId = allRows.ToDictionary(r => r.LeaseId);

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
        // Catch AccountPeriodLockedException per-item → Excluded (period locked).
        var items = new List<BulkRunItem>(selectedTargetIds.Count);
        items.AddRange(skippedItems);

        // Post one at a time so we can catch per-item exceptions cleanly.
        foreach (var intent in intents)
        {
            BulkRunItem item;
            try
            {
                var resultMap = await posting.PostRentChargesAsync([intent], ct);
                var entryId = resultMap[intent.LeaseId];
                var snapshot = JsonSerializer.Serialize(new
                {
                    entryId,
                    sourceRef = intent.SourceRef,
                    amount = intent.Amount,
                    prorated = intent.Amount != byLeaseId[intent.LeaseId].Rent,
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
            items.Add(item);
        }

        return items;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string SourceRef(RunPeriod period, Guid leaseId) =>
        $"rent:{period.Key}:lease={leaseId}";

    /// <summary>
    /// Checks for DuplicateSourceRefException without referencing Accounting assembly
    /// (ADR-007: Operations references SharedKernel only — no Accounting types).
    /// The exception type name is sufficient for an exception filter.
    /// </summary>
    private static bool IsDuplicateSourceRef(Exception ex) =>
        ex.GetType().Name == "DuplicateSourceRefException";

    /// <summary>
    /// Checks for AccountPeriodLockedException without referencing Accounting assembly.
    /// </summary>
    private static bool IsPeriodLocked(Exception ex) =>
        ex.GetType().Name == "AccountPeriodLockedException";
}
