using System.Text.Json;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// <see cref="IRunStrategy"/> for <see cref="RunType.Disbursement"/>. Previews and confirms the
/// monthly owner disbursement run, folding the management-fee assessment into the same batch
/// (ADR-018).
/// <para>
/// <b>Math (per owner, equity-at-run-time — D3):</b>
/// <list type="bullet">
///   <item><c>fee = MgmtFee.Compute(equity, effectiveBps)</c> (ADR-018 rounding: AwayFromZero).</item>
///   <item><c>netBeforeReserve = equity − fee</c>.</item>
///   <item><c>disburse = netBeforeReserve − reserve</c>.</item>
/// </list>
/// <b>Exclusions (preview + confirm):</b>
/// <list type="bullet">
///   <item><c>equity ≤ 0</c> → <c>"non_positive_equity"</c>.</item>
///   <item><c>disburse ≤ 0</c> → <c>"below_reserve_floor"</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Posting order per owner on confirm:</b> <c>ManagementFeeAssessed</c> FIRST (only when
/// <c>fee &gt; 0</c>), then <c>OwnerDisbursed</c>. The fee posting reduces owner equity before
/// the existing <c>GuardReserveFloorAsync</c> backstop checks the reserve floor.
/// </para>
/// <para>
/// <b>Source-ref convention (ADR-019):</b>
/// <list type="bullet">
///   <item>Fee leg: <c>disbursement-fee:{year}-{month:00}:owner={ownerId}</c>.</item>
///   <item>Disburse leg: <c>disbursement:{year}-{month:00}:owner={ownerId}</c>.</item>
/// </list>
/// AlreadyDone is checked against the DISBURSEMENT source ref only (the disburse leg is the
/// authoritative idempotency key; if the fee was posted but disburse was not, that is a
/// partial failure requiring investigation — not treated as done).
/// </para>
/// <para>
/// <b>Phase-1 simplification (owner-level bps — ADR-018):</b> The disbursement aggregates
/// each owner's entire equity across all properties; property-level fee overrides are NOT
/// applied. Only <c>owners.default_mgmt_fee_bps</c> is used (the <c>propertyId = null</c>
/// resolution path). Property-precise fees require per-property equity decomposition (future work).
/// </para>
/// <para>
/// <b>Exceptions caught per-item on confirm (ADR-007 — type-name string match):</b>
/// <list type="bullet">
///   <item><c>DuplicateSourceRefException</c> → <see cref="RunItemStatus.Skipped"/>.</item>
///   <item><c>AccountPeriodLockedException</c> → <see cref="RunItemStatus.Excluded"/>.</item>
///   <item><c>PeriodClosedException</c> → <see cref="RunItemStatus.Excluded"/>.</item>
///   <item><c>ReserveFloorException</c> → <see cref="RunItemStatus.Excluded"/> (posting-time backstop).</item>
/// </list>
/// </para>
/// </summary>
public sealed class DisbursementRunStrategy(
    IOwnerDisbursementData ownerData,
    IOwnerEquityBalances equityBalances,
    IBankAccountInfo bankInfo,
    IPostedSourceRefs postedRefs) : IRunStrategy
{
    private const string Basis = "cash";

    /// <inheritdoc />
    public RunType RunType => RunType.Disbursement;

    /// <inheritdoc />
    public async Task<RunPreview> PreviewAsync(RunPeriod period, CancellationToken ct)
    {
        var owners = await ownerData.GetAsync(ct);
        if (owners.Count == 0)
        {
            return new RunPreview(RunType.Disbursement, period, [], []);
        }

        var ownerIds = owners.Select(o => o.OwnerId).ToList();
        var equityMap = await equityBalances.GetAsync(ownerIds, Basis, ct);

        // Check already-posted disbursement source refs (the authoritative idempotency leg).
        var disburseKeys = owners.Select(o => DisburseSourceRef(period, o.OwnerId)).ToList();
        var alreadyPosted = disburseKeys.Count > 0
            ? await postedRefs.GetExistingAsync(disburseKeys, ct)
            : (IReadOnlySet<string>)new HashSet<string>();

        var previewRows = new List<PreviewRow>(owners.Count);

        foreach (var owner in owners)
        {
            var equity = equityMap.GetValueOrDefault(owner.OwnerId, 0m);
            var fee = MgmtFee.Compute(equity, owner.DefaultMgmtFeeBps);
            var netBeforeReserve = equity - fee;
            var disburse = netBeforeReserve - owner.ReserveAmount;
            var disburseKey = DisburseSourceRef(period, owner.OwnerId);
            var alreadyDone = alreadyPosted.Contains(disburseKey);

            var detail = new Dictionary<string, string>
            {
                ["equity"] = equity.ToString("F2"),
                ["fee"] = fee.ToString("F2"),
                ["netBeforeReserve"] = netBeforeReserve.ToString("F2"),
                ["reserve"] = owner.ReserveAmount.ToString("F2"),
            };

            string? excludedReason = null;
            decimal rowAmount = 0m;

            if (equity <= 0m)
            {
                excludedReason = "non_positive_equity";
            }
            else if (disburse <= 0m)
            {
                excludedReason = "below_reserve_floor";
            }
            else
            {
                rowAmount = disburse;
            }

            previewRows.Add(new PreviewRow(
                TargetKind: RunTargetKind.Owner,
                TargetId: owner.OwnerId,
                Label: owner.Name,
                Amount: rowAmount,
                AlreadyDone: alreadyDone,
                ExcludedReason: excludedReason,
                Detail: detail));
        }

        return new RunPreview(RunType.Disbursement, period, previewRows, []);
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

        // Re-fetch owner data and equity at confirm time (preview may be stale).
        var allOwners = await ownerData.GetAsync(ct);
        var byOwnerId = allOwners.ToDictionary(o => o.OwnerId);

        var ownerIdsInScope = selectedTargetIds.Where(id => byOwnerId.ContainsKey(id)).ToList();
        var equityMap = ownerIdsInScope.Count > 0
            ? await equityBalances.GetAsync(ownerIdsInScope, Basis, ct)
            : (IReadOnlyDictionary<Guid, decimal>)new Dictionary<Guid, decimal>();

        var (operatingBankId, bankDisplay) = await bankInfo.GetOperatingTrustAsync(ct);
        var chargeDate = new DateOnly(period.Year, period.Month, 1);

        // Build intents for selected, eligible owners.
        var intents = new List<DisbursementIntent>(selectedTargetIds.Count);
        var skippedItems = new List<BulkRunItem>();

        foreach (var ownerId in selectedTargetIds)
        {
            if (!byOwnerId.TryGetValue(ownerId, out var owner))
            {
                skippedItems.Add(BulkRunItem.Create(
                    run.Id, RunTargetKind.Owner, ownerId,
                    RunItemStatus.Excluded, 0m,
                    JsonSerializer.Serialize(new { reason = "owner_not_found" }),
                    run.CreatedAt));
                continue;
            }

            var equity = equityMap.GetValueOrDefault(ownerId, 0m);
            var fee = MgmtFee.Compute(equity, owner.DefaultMgmtFeeBps);
            var netBeforeReserve = equity - fee;
            var disburse = netBeforeReserve - owner.ReserveAmount;

            if (equity <= 0m)
            {
                skippedItems.Add(BulkRunItem.Create(
                    run.Id, RunTargetKind.Owner, ownerId,
                    RunItemStatus.Excluded, 0m,
                    JsonSerializer.Serialize(new { reason = "non_positive_equity", equity }),
                    run.CreatedAt));
                continue;
            }

            if (disburse <= 0m)
            {
                skippedItems.Add(BulkRunItem.Create(
                    run.Id, RunTargetKind.Owner, ownerId,
                    RunItemStatus.Excluded, 0m,
                    JsonSerializer.Serialize(new
                    {
                        reason = "below_reserve_floor",
                        equity,
                        fee,
                        netBeforeReserve,
                        reserve = owner.ReserveAmount,
                        disburse,
                    }),
                    run.CreatedAt));
                continue;
            }

            var description = $"Disbursement {period.Key} — {owner.Name}";
            var bankWithdrawalRef = $"check/ACH {period.Key} {owner.Name}";

            intents.Add(new DisbursementIntent(
                OwnerId: ownerId,
                PropertyId: null,
                MgmtFee: fee,
                DisburseAmount: disburse,
                Reserve: owner.ReserveAmount,
                Date: chargeDate,
                OperatingBankId: operatingBankId,
                Description: description,
                FeeSourceRef: FeeSourceRef(period, ownerId),
                DisburseSourceRef: DisburseSourceRef(period, ownerId)));
        }

        // Post one at a time so we can catch per-item exceptions cleanly.
        var items = new List<BulkRunItem>(selectedTargetIds.Count);
        items.AddRange(skippedItems);

        foreach (var intent in intents)
        {
            BulkRunItem item;
            var feeRef = intent.FeeSourceRef;
            var disburseRef = intent.DisburseSourceRef;

            try
            {
                var resultMap = await posting.PostDisbursementsAsync([intent], ct);
                var result = resultMap[intent.OwnerId];

                var snapshot = JsonSerializer.Serialize(new
                {
                    feeEntryId = result.FeeEntryId,
                    disbursementEntryId = result.DisbursementEntryId,
                    feeSourceRef = feeRef,
                    disburseSourceRef = disburseRef,
                    fee = intent.MgmtFee,
                    disburse = intent.DisburseAmount,
                    reserve = intent.Reserve,
                    bankWithdrawalRef = $"check/ACH {new RunPeriod(intent.Date.Year, intent.Date.Month).Key} {byOwnerId[intent.OwnerId].Name}",
                });

                item = BulkRunItem.Create(
                    run.Id, RunTargetKind.Owner, intent.OwnerId,
                    RunItemStatus.Posted, intent.DisburseAmount, snapshot, run.CreatedAt);
            }
            catch (Exception ex) when (IsDuplicateSourceRef(ex))
            {
                var snapshot = JsonSerializer.Serialize(new
                {
                    disburseSourceRef = disburseRef,
                    reason = "duplicate_source_ref",
                });
                item = BulkRunItem.Create(
                    run.Id, RunTargetKind.Owner, intent.OwnerId,
                    RunItemStatus.Skipped, 0m, snapshot, run.CreatedAt);
            }
            catch (Exception ex) when (IsPeriodLocked(ex))
            {
                var snapshot = JsonSerializer.Serialize(new
                {
                    disburseSourceRef = disburseRef,
                    reason = "period_locked",
                });
                item = BulkRunItem.Create(
                    run.Id, RunTargetKind.Owner, intent.OwnerId,
                    RunItemStatus.Excluded, 0m, snapshot, run.CreatedAt);
            }
            catch (Exception ex) when (IsPeriodClosed(ex))
            {
                var snapshot = JsonSerializer.Serialize(new
                {
                    disburseSourceRef = disburseRef,
                    reason = "period_closed",
                });
                item = BulkRunItem.Create(
                    run.Id, RunTargetKind.Owner, intent.OwnerId,
                    RunItemStatus.Excluded, 0m, snapshot, run.CreatedAt);
            }
            catch (Exception ex) when (IsReserveFloor(ex))
            {
                // Posting-time backstop (GuardReserveFloorAsync). Preview already excluded
                // below-reserve owners; this catch is the safety net for equity that changed
                // between preview and confirm.
                var snapshot = JsonSerializer.Serialize(new
                {
                    disburseSourceRef = disburseRef,
                    reason = "reserve_floor",
                });
                item = BulkRunItem.Create(
                    run.Id, RunTargetKind.Owner, intent.OwnerId,
                    RunItemStatus.Excluded, 0m, snapshot, run.CreatedAt);
            }
            items.Add(item);
        }

        return items;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string FeeSourceRef(RunPeriod period, Guid ownerId) =>
        $"disbursement-fee:{period.Key}:owner={ownerId}";

    private static string DisburseSourceRef(RunPeriod period, Guid ownerId) =>
        $"disbursement:{period.Key}:owner={ownerId}";

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
    /// An OwnerDisbursed posting into a closed accounting period raises this; the item is Excluded.
    /// </summary>
    private static bool IsPeriodClosed(Exception ex) =>
        ex.GetType().Name == "PeriodClosedException";

    /// <summary>
    /// Checks for ReserveFloorException without referencing Accounting assembly (ADR-007).
    /// GuardReserveFloorAsync throws this when owner equity after fee is below the reserve floor;
    /// caught here as the safety net even though preview already excludes below-reserve owners.
    /// </summary>
    private static bool IsReserveFloor(Exception ex) =>
        ex.GetType().Name == "ReserveFloorException";
}
