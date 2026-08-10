using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// <see cref="IRunStrategy"/> for <see cref="RunType.Disbursement"/>. Previews the monthly owner
/// disbursement run and plans it, folding the management-fee assessment into the same intent
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
/// <b>Posting refusals, recorded per-item by the engine.</b> The strategy sees none of them: the
/// posting port returns a <see cref="Contracts.PostStatus"/> and <see cref="RunEngine"/> maps it.
/// <see cref="Contracts.PostStatus.ReserveFloor"/> is the one of these that is disbursement-specific
/// — the posting-time backstop for equity that moved between preview and confirm, where the
/// <c>disburse &lt;= 0</c> exclusion above is the same rule applied to the data the plan was built on.
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
    public async Task<IReadOnlyList<RunPlanItem>> PlanAsync(
        RunPeriod period,
        IReadOnlyList<Guid> selectedTargetIds,
        CancellationToken ct)
    {
        // Re-fetch owner data and equity at confirm time (preview may be stale).
        var allOwners = await ownerData.GetAsync(ct);
        var byOwnerId = allOwners.ToDictionary(o => o.OwnerId);

        var ownerIdsInScope = selectedTargetIds.Where(id => byOwnerId.ContainsKey(id)).ToList();
        var equityMap = ownerIdsInScope.Count > 0
            ? await equityBalances.GetAsync(ownerIdsInScope, Basis, ct)
            : (IReadOnlyDictionary<Guid, decimal>)new Dictionary<Guid, decimal>();

        var (operatingBankId, _) = await bankInfo.GetOperatingTrustAsync(ct);
        var chargeDate = new DateOnly(period.Year, period.Month, 1);
        var plan = new List<RunPlanItem>(selectedTargetIds.Count);

        foreach (var ownerId in selectedTargetIds)
        {
            if (!byOwnerId.TryGetValue(ownerId, out var owner))
            {
                plan.Add(Exclude(ownerId, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["reason"] = "owner_not_found",
                }));
                continue;
            }

            var equity = equityMap.GetValueOrDefault(ownerId, 0m);
            var fee = MgmtFee.Compute(equity, owner.DefaultMgmtFeeBps);
            var netBeforeReserve = equity - fee;
            var disburse = netBeforeReserve - owner.ReserveAmount;

            if (equity <= 0m)
            {
                plan.Add(Exclude(ownerId, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["reason"] = "non_positive_equity",
                    ["equity"] = equity,
                }));
                continue;
            }

            if (disburse <= 0m)
            {
                plan.Add(Exclude(ownerId, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["reason"] = "below_reserve_floor",
                    ["equity"] = equity,
                    ["fee"] = fee,
                    ["netBeforeReserve"] = netBeforeReserve,
                    ["reserve"] = owner.ReserveAmount,
                    ["disburse"] = disburse,
                }));
                continue;
            }

            var description = $"Disbursement {period.Key} — {owner.Name}";
            var feeRef = FeeSourceRef(period, ownerId);
            var disburseRef = DisburseSourceRef(period, ownerId);

            plan.Add(new PlannedPosting(
                TargetKind: RunTargetKind.Owner,
                TargetId: ownerId,
                Intent: new DisbursementIntent(
                    OwnerId: ownerId,
                    PropertyId: null,
                    MgmtFee: fee,
                    DisburseAmount: disburse,
                    Reserve: owner.ReserveAmount,
                    Date: chargeDate,
                    OperatingBankId: operatingBankId,
                    Description: description,
                    FeeSourceRef: feeRef,
                    DisburseSourceRef: disburseRef),
                Amount: disburse,
                PostedDetail: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["feeSourceRef"] = feeRef,
                    ["disburseSourceRef"] = disburseRef,
                    ["fee"] = fee,
                    ["disburse"] = disburse,
                    ["reserve"] = owner.ReserveAmount,
                    ["bankWithdrawalRef"] = $"check/ACH {period.Key} {owner.Name}",
                },
                RefusedDetail: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["disburseSourceRef"] = disburseRef,
                }));
        }

        return plan;

        // Every disbursement refusal the strategy itself decides is an exclusion: an owner with no
        // equity or below the floor has nothing to post, as opposed to something already posted.
        static PlannedExclusion Exclude(Guid ownerId, Dictionary<string, object?> detail) =>
            new(RunTargetKind.Owner, ownerId, RunItemStatus.Excluded, detail);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>ADR-019 §2. Built here, not derived — see the note on <c>RentRunStrategy.SourceRef</c>.</summary>
    private static string FeeSourceRef(RunPeriod period, Guid ownerId) =>
        $"disbursement-fee:{period.Key}:owner={ownerId}";

    /// <inheritdoc cref="FeeSourceRef"/>
    private static string DisburseSourceRef(RunPeriod period, Guid ownerId) =>
        $"disbursement:{period.Key}:owner={ownerId}";
}
