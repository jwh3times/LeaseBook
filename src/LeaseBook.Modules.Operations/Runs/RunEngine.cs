using System.Text.Json;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.SharedKernel.Observability;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// The shared run pipeline (ADR-019 / M6 WP-1). Resolves the right <see cref="IRunStrategy"/> by
/// <see cref="RunType"/>, asks it what the run should do, posts what it asked for, persists the
/// <see cref="BulkRun"/> header + <see cref="BulkRunItem"/> rows, and returns a <see cref="RunResult"/>.
/// <para>
/// <b>Transaction model:</b> <c>ConfirmAsync</c> does NOT open a new transaction. It must be called
/// inside the ambient org-scoped transaction (set up by the request middleware or
/// <c>OrgScopedExecutor</c>). Postings go through <see cref="IBatchPosting"/> under that same
/// transaction; all writes are committed together. Every per-item refusal comes back from the port as
/// a <see cref="PostOutcome"/> rather than an exception, so the loop below records each and carries
/// on; anything that does throw is not per-item — a lost connection, a rolled-back transaction — and
/// is deliberately not caught.
/// </para>
/// <para>
/// <b>Capability freeze:</b> <c>ConfirmAsync</c> resolves the capability set exactly once, at its own
/// entry, and nothing downstream resolves it again. One run therefore decides every item under one
/// set even if an operator flips a flag mid-run — and since the strategy is never handed the set at
/// all (ADR-019 §4a, amended 2026-08-09), a capability can decide whether a run happens and never what
/// it produces. Under a future chunked run confirmation (ADR-019's revisit trigger) this method is what
/// resumes, so it is also what must carry the snapshot across a chunk boundary.
/// </para>
/// <para>
/// <b>The preview → run confirmation window.</b> The freeze above makes one run confirmation internally consistent; it
/// says nothing about the gap before it. <c>PreviewAsync</c> stamps the resolved version onto the
/// <see cref="RunPreview"/>, the run confirmation echoes it back, and <c>ConfirmAsync</c> compares it against
/// the set it resolves itself — optimistic concurrency, the same shape as an ETag. The operator
/// selected target <i>ids</i>, but the <i>amounts</i> they approved were the preview's, so a set that
/// moved in between makes the run confirmation a different operation from the one they authorized. Nothing
/// about the preview is persisted to support this: the token is derived from state, not stored.
/// </para>
/// <para>
/// <b>The cross-run window.</b> The two guards above make ONE run consistent. A period is
/// routinely built by more than one run, because the designed recovery path IS a re-run:
/// <c>source_ref</c> uniqueness lands already-posted items as <c>Skipped</c> (ADR-019 §2). So run
/// 1 confirms a selection while a money-path capability is off, the flag flips, and run 2 confirms
/// the remainder while it is on — both internally consistent, the period not. <c>ConfirmAsync</c>
/// therefore reads the money-path state recorded by the most recent prior run for the same
/// <c>(org, run type, period)</c> and rejects a run confirmation that would disagree with it, unless the
/// caller explicitly acknowledges the change — in which case the acknowledgement, and the state it
/// overrode, are recorded in <c>summary_json</c>.
/// </para>
/// <para>
/// <b>Audit:</b> <c>AppDbContext.SaveChangesAsync</c> automatically writes one <c>audit_events</c>
/// row per entity insert (including the <see cref="BulkRun"/> header), satisfying the "one audit row
/// per committed run" requirement without any explicit audit write here.
/// </para>
/// </summary>
public sealed class RunEngine(
    DbContext db,
    IEnumerable<IRunStrategy> strategies,
    IBatchPosting posting,
    TimeProvider clock,
    IRunPeriodLock periodLock,
    ICapabilitySnapshot capabilitySnapshot)
{
    private readonly IReadOnlyDictionary<RunType, IRunStrategy> _strategies =
        strategies.ToDictionary(s => s.RunType);

    /// <summary>
    /// Returns a preview of what would be posted for the given <paramref name="period"/>. Delegates
    /// the rows entirely to the strategy; no mutations occur, and nothing about the preview is
    /// persisted — the version token below is derived, not stored, so there is no preview row, no
    /// table and no migration behind it.
    /// <para>
    /// <b>The capability set is resolved BEFORE the strategy computes rows, not after.</b> Both
    /// instants are inside the caller's ambient transaction, and READ COMMITTED means a flip
    /// committed by another connection is visible partway through. Resolving first means a flip
    /// during the row computation leaves the operator holding a token that no longer matches, so the
    /// confirm is rejected; resolving afterwards would stamp the post-flip version onto pre-flip
    /// rows and let exactly that change pass unnoticed.
    /// </para>
    /// </summary>
    public async Task<RunPreview> PreviewAsync(RunType runType, RunPeriod period, CancellationToken ct)
    {
        var strategy = ResolveStrategy(runType);

        // Same port, same derivation, as the run confirmation below. Deliberately NOT the cached member: a
        // token served from a 30-second cache would disagree with the run confirmation's durable read for up
        // to that long after any flip, rejecting confirms for a change that had already settled.
        var capabilities = await capabilitySnapshot.ResolveDurableAsync(ct);
        var preview = await strategy.PreviewAsync(period, ct);

        return preview with { CapabilitiesVersion = capabilities.Version };
    }

    /// <summary>
    /// Confirms the run for the given <paramref name="selectedTargetIds"/>: calls the strategy's
    /// <c>ConfirmAsync</c>, persists the <see cref="BulkRun"/> header + <see cref="BulkRunItem"/>
    /// rows, emits a telemetry span, and returns a <see cref="RunResult"/>.
    /// Must be called inside the ambient org-scoped transaction.
    /// </summary>
    /// <param name="expectedCapabilitiesVersion">
    /// The <see cref="RunPreview.CapabilitiesVersion"/> the operator was shown, echoed back —
    /// optimistic concurrency in the shape of an ETag. The comparison happens HERE, server-side,
    /// against the set this method resolves itself; the caller only carries the value.
    /// <para>
    /// <c>null</c> means "there is no preview to honour" and skips the comparison. That is for
    /// in-process callers that confirm without having shown anyone a preview (seed/fixture paths, a
    /// re-confirm in a fresh transaction). It is not an escape hatch on the HTTP surface: the
    /// endpoint rejects a request that omits the token, so a client cannot opt out of the check by
    /// leaving a field off. The parameter has no default precisely so that every call site has to
    /// state which of the two it is.
    /// </para>
    /// </param>
    /// <param name="acknowledgeCapabilityChange">
    /// The operator's explicit "run it anyway" — for the CROSS-RUN check only. It does not weaken
    /// the preview/confirm comparison above, which is always the caller's own mistake to re-take.
    /// When a prior run for this period recorded a different money-path state, true lets the run confirmation
    /// proceed and records both the acknowledgement and the state it overrode in <c>summary_json</c>.
    /// <para>
    /// No default, like the token above, so every call site states which it is. In-process callers
    /// that legitimately re-run a period (seeders, fixtures) pass false and simply never trip the
    /// check, because nothing flipped a money-path capability under them.
    /// </para>
    /// </param>
    /// <exception cref="CapabilitiesChangedException">
    /// <paramref name="expectedCapabilitiesVersion"/> is non-null and differs from the set resolved
    /// at entry.
    /// </exception>
    /// <exception cref="CapabilitiesChangedSincePriorRunException">
    /// A prior committed run for the same <c>(org, run type, period)</c> recorded a different
    /// money-path capability state and <paramref name="acknowledgeCapabilityChange"/> is false.
    /// </exception>
    public async Task<RunResult> ConfirmAsync(
        RunType runType,
        RunPeriod period,
        IReadOnlyList<Guid> selectedTargetIds,
        string? expectedCapabilitiesVersion,
        bool acknowledgeCapabilityChange,
        CancellationToken ct)
    {
        using var activity = LeaseBookTelemetry.Source.StartActivity($"BulkRun.{runType}");
        activity?.SetTag("run_type", runType.ToString());
        activity?.SetTag("period", period.Key);
        activity?.SetTag("selected_count", selectedTargetIds.Count);

        var strategy = ResolveStrategy(runType);

        // READ COMMITTED does not protect an absent prior-run row: two confirms can both observe
        // "none", resolve different capability state, and commit disjoint targets for one period.
        // Take the transaction advisory lock before either the durable capability read or the prior
        // run query. The second transaction then resumes only after the first run is committed and
        // visible, so it must compare against that frozen money-path state.
        await periodLock.AcquireAsync(runType, period, ct);

        // Create the run header — NOT yet added to the change tracker. We add it after the strategy
        // finishes so that any intermediate db.SaveChangesAsync calls inside posting (PostingService
        // saves journal entries) don't accidentally include the BulkRun in those saves (the same
        // AppDbContext is used for both, so adding here would enqueue it for the next save).
        var run = BulkRun.Create(runType, period.Year, period.Month, "{}", clock.GetUtcNow().UtcDateTime);

        // Resolve ONCE, inside the ambient transaction, and freeze for the whole run. Not
        // cache-served: a money-path kill switch must be effective immediately, and a strictly
        // consistent read here makes the freeze trivially correct, because it happens in the same
        // transaction as the posts it governs.
        //
        // The resolve is at CONFIRM ENTRY, not at transaction start. Those are different instants:
        // OrgContextMiddleware opens the transaction before the endpoint handler runs, so a snapshot
        // taken there would predate the run confirmation the operator actually asked for.
        var capabilities = await capabilitySnapshot.ResolveDurableAsync(ct);
        activity?.SetTag("capabilities_version", capabilities.Version);

        // Close the preview → run confirmation window, against the ONE set resolved above. No second resolve:
        // the freeze is that confirm reads the capability state exactly once, and a comparison that
        // re-read it would both break that and compare a value against itself.
        //
        // Ordinal, because the token is an opaque digest — a culture-sensitive comparison on a
        // Base64Url string is meaningless at best. Before the strategy runs, so a rejection posts
        // nothing at all rather than part of a run.
        if (expectedCapabilitiesVersion is not null &&
            !string.Equals(expectedCapabilitiesVersion, capabilities.Version, StringComparison.Ordinal))
        {
            throw new CapabilitiesChangedException();
        }

        // Close the CROSS-RUN window, second. The order between the two 409s is deliberate: a stale
        // token is auto-recoverable (the SPA refetches the preview and the operator clicks again),
        // while this one cannot be cleared by re-previewing at all — it asks for a decision instead.
        // Running the cheap, self-service rejection first means an operator who trips both makes that
        // decision holding a preview that matches the CURRENT state; acknowledging while still holding
        // a stale preview would authorize amounts they never saw.
        //
        // A READ, not a resolve: the set frozen above is still the only capability read in this
        // method (RunEngineTests pins that count at one per run confirmation). bulk_runs is Operations' own
        // table and the query runs on the ambient RLS transaction, so it sees this org's runs and no
        // other org's without a predicate this method could forget.
        var moneyPathState = capabilities.MoneyPathState();
        var priorMoneyPathState = await ReadPriorMoneyPathStateAsync(runType, period, ct);
        var overrodePriorState =
            priorMoneyPathState is not null &&
            !priorMoneyPathState.SequenceEqual(moneyPathState, StringComparer.Ordinal);

        if (overrodePriorState && !acknowledgeCapabilityChange)
        {
            // Same conflict, same wire code, two different remedies. When the REGISTRY's set of
            // money-path names moved between the runs — a capability added or removed — the earlier
            // state cannot be restored by any operator action, because those names come from source
            // code and not from feature_flags. The message must not then offer a restore the operator
            // would go hunting for. Either direction counts: an addition is as period-breaking as a
            // removal, and is the commoner deploy.
            //
            // Neither direction is filtered out of the comparison itself: posting while a gate was
            // live and posting before it existed are two behaviours, and the difference is real.
            throw capabilities.RegistryMoved(priorMoneyPathState!)
                ? CapabilitiesChangedSincePriorRunException.RegistryMoved()
                : CapabilitiesChangedSincePriorRunException.StateMoved();
        }

        activity?.SetTag("capability_change_acknowledged", overrodePriorState);

        // Ask the strategy what this run should do, then do it. The split is the point: the strategy
        // knows which targets are eligible and what each one is worth, and nothing else — no loop, no
        // outcome mapping, no item construction. It is also handed no capability set, which is what
        // makes "a capability cannot move an amount" structural rather than a rule (see IRunStrategy).
        var plan = await strategy.PlanAsync(period, selectedTargetIds, ct);
        var items = await ExecutePlanAsync(run, plan, ct);

        // Compute summary, patch onto run, then add to the change tracker for a single save.
        int posted = 0, skipped = 0, excluded = 0;
        decimal total = 0m;
        foreach (var item in items)
        {
            switch (item.Status)
            {
                case RunItemStatus.Posted:
                    posted++;
                    total += item.Amount;
                    break;
                case RunItemStatus.Skipped:
                    skipped++;
                    break;
                case RunItemStatus.Excluded:
                    excluded++;
                    break;
            }
        }

        // Patch the summary JSON before the first save (SetSummaryJson is only valid in Added state,
        // and RevokeAppendOnly("bulk_runs") removes UPDATE entirely, so a later patch is impossible
        // rather than merely awkward). The capability state goes in here so a committed run states
        // which set it ran under: `capabilities` is the version token the cross-run consistency check
        // compares, and `capabilitiesEnabled` is the human-readable half, since the version is an
        // opaque hash that nobody can read a state back out of.
        //
        // A NAMED record, not an anonymous object: capabilitiesMoneyPath is read back by the guard
        // above, whose "a field-less run must predate the field" reasoning holds only while EVERY
        // committed BulkRun writes it. RunSummary has no optional member, so a future writer that
        // omits it does not compile. See RunSummary for the full argument.
        var summary = new RunSummary(
            posted,
            skipped,
            excluded,
            total,
            Capabilities: capabilities.Version,
            CapabilitiesEnabled: capabilities.EnabledNames(),
            CapabilitiesMoneyPath: moneyPathState,
            CapabilityChangeAcknowledged: overrodePriorState,
            CapabilityChangeFrom: overrodePriorState ? priorMoneyPathState : null);
        run.SetSummaryJson(summary.ToJson());

        // Now add everything to the change tracker for a single atomic save.
        db.Set<BulkRun>().Add(run);
        foreach (var item in items)
        {
            db.Set<BulkRunItem>().Add(item);
        }

        await db.SaveChangesAsync(ct);

        var result = new RunResult(run.Id, posted, skipped, excluded, total);

        activity?.SetTag("posted", posted);
        activity?.SetTag("skipped", skipped);
        activity?.SetTag("excluded", excluded);

        return result;
    }

    /// <summary>
    /// Drives the plan and returns the rows to persist. One posting attempt per
    /// <see cref="PlannedPosting"/>, in plan order, on the caller's ambient transaction.
    /// <para>
    /// <b>This loop used to live in every strategy.</b> Three copies of it, three catch ladders, three
    /// sets of <c>BulkRunItem.Create</c> calls and three copies of the serialization — none of it
    /// domain knowledge, and a fourth run type had to reproduce all of it correctly from memory. What
    /// a strategy actually knows is which targets are eligible and what each is worth; that is what a
    /// <see cref="RunPlanItem"/> carries and all it carries.
    /// </para>
    /// <para>
    /// <b>Refusals do not abort the run.</b> Every <see cref="PostStatus"/> other than
    /// <see cref="PostStatus.Posted"/> is a per-item outcome the port returns rather than throws, so a
    /// duplicate source ref or a locked period costs one item and no more. A genuine exception —
    /// connection loss, a rolled-back transaction — is not caught here, because it is not per-item and
    /// the run's own transaction is what unwinds it.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<BulkRunItem>> ExecutePlanAsync(
        BulkRun run, IReadOnlyList<RunPlanItem> plan, CancellationToken ct)
    {
        var items = new List<BulkRunItem>(plan.Count);

        foreach (var planned in plan)
        {
            switch (planned)
            {
                case PlannedExclusion exclusion:
                    if (exclusion.Status == RunItemStatus.Posted)
                    {
                        throw new InvalidOperationException(
                            $"A PlannedExclusion for target {exclusion.TargetId} claims Posted status, " +
                            "but nothing was posted for it. An item that posts is a PlannedPosting.");
                    }

                    items.Add(BulkRunItem.Create(
                        run.Id, exclusion.TargetKind, exclusion.TargetId, exclusion.Status, 0m,
                        Serialize(exclusion.Detail), run.CreatedAt));
                    break;

                case PlannedPosting intended:
                    items.Add(await PostPlannedAsync(run, intended, ct));
                    break;

                // RunPlanItem's constructor is private protected, so a third case can only be added in
                // this module and in the same file as the other two. This arm is the reminder to come
                // back here when that happens, not a live branch.
                default:
                    throw new InvalidOperationException(
                        $"No execution branch for run plan item {planned.GetType().Name}.");
            }
        }

        return items;
    }

    /// <summary>
    /// Posts one planned intent and turns the outcome into the row that records it. The engine
    /// contributes exactly four keys to <c>snapshot_json</c> — <c>entryId</c>, <c>feeEntryId</c> and
    /// <c>reason</c> — because only it has the outcome; everything else on the item is the strategy's
    /// own vocabulary, copied through unread.
    /// </summary>
    private async Task<BulkRunItem> PostPlannedAsync(
        BulkRun run, PlannedPosting planned, CancellationToken ct)
    {
        var outcome = await posting.PostAsync(planned.Intent, ct);

        if (outcome.Status == PostStatus.Posted)
        {
            var detail = new Dictionary<string, object?>(planned.PostedDetail, StringComparer.Ordinal)
            {
                ["entryId"] = outcome.EntryId,
            };

            // Only a disbursement that actually assessed a fee has one; adding a null for every other
            // intent would put a field in the run log that means nothing.
            if (outcome.FeeEntryId is not null)
            {
                detail["feeEntryId"] = outcome.FeeEntryId;
            }

            return BulkRunItem.Create(
                run.Id, planned.TargetKind, planned.TargetId, RunItemStatus.Posted, planned.Amount,
                Serialize(detail), run.CreatedAt, resultingJournalEntryId: outcome.EntryId);
        }

        var (status, reason) = Classify(outcome.Status);
        var refusal = new Dictionary<string, object?>(planned.RefusedDetail, StringComparer.Ordinal)
        {
            ["reason"] = reason,
        };

        // Zero, not the planned amount: nothing was posted, so nothing may count toward the run total.
        return BulkRunItem.Create(
            run.Id, planned.TargetKind, planned.TargetId, status, 0m,
            Serialize(refusal), run.CreatedAt);
    }

    /// <summary>
    /// The one place a posting refusal becomes a run-item status and an operator-readable reason. It
    /// was three places — one per strategy — each mapping the same four cases identically, and each
    /// free to drift.
    /// </summary>
    private static (RunItemStatus Status, string Reason) Classify(PostStatus status) => status switch
    {
        // The run is being repeated. ADR-019 §2 makes that the designed recovery path, so it is a
        // skip: the work exists, this run simply did not do it.
        PostStatus.DuplicateSourceRef => (RunItemStatus.Skipped, "duplicate_source_ref"),
        PostStatus.PeriodLocked => (RunItemStatus.Excluded, "period_locked"),
        PostStatus.PeriodClosed => (RunItemStatus.Excluded, "period_closed"),
        PostStatus.ReserveFloor => (RunItemStatus.Excluded, "reserve_floor"),

        // Posted is handled by the caller and never reaches here; anything else is a PostStatus added
        // without a decision about what it means for a run item, which is a money-path question.
        _ => throw new InvalidOperationException(
            $"No run-item classification for posting outcome {status}."),
    };

    private static string Serialize(IReadOnlyDictionary<string, object?> detail) =>
        JsonSerializer.Serialize(detail);

    /// <summary>
    /// The money-path capability state recorded by the most recent committed run for this
    /// <c>(org, run type, period)</c>, or <c>null</c> when there is nothing to compare against.
    /// Reads <c>bulk_runs</c> — Operations' own table (ADR-007 allows a module its own reads) — on
    /// the ambient RLS transaction.
    /// <para>
    /// <b>Only the most recent run.</b> Every run is guarded against its predecessor, so agreeing
    /// with the latest implies agreeing with all of them — except across a deliberate
    /// acknowledgement, which is exactly where the chain SHOULD restart. The acknowledged state is
    /// the period's state from that point on; re-asserting a superseded one would leave the operator
    /// unable to finish the period without acknowledging every remaining run.
    /// </para>
    /// <para>
    /// <b>Null for a run that recorded no state, rather than an assumed empty one.</b> Runs committed
    /// before this field existed cannot be compared. Reading an absent field as "no money-path
    /// capabilities were in effect" would be a fabrication, and it would reject every period holding a
    /// pre-ADR-028 run — the seeded demo org included — blocking the very re-run ADR-019 §2 makes the
    /// recovery path, for no gain: the prior run is already committed and rejecting this one cannot
    /// unwind it. Self-limiting, because every run written from here on records the field.
    /// </para>
    /// <para>
    /// <b>Scoped to one run type</b>, matching the existing
    /// <c>ix_bulk_runs_org_id_run_type_period_year_period_month</c> index. <c>source_ref</c>
    /// uniqueness — and so the re-run recovery path this guard protects — is per event kind, so a rent
    /// run and a late-fee run in one month are two computations, not one computed twice.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>?> ReadPriorMoneyPathStateAsync(
        RunType runType, RunPeriod period, CancellationToken ct)
    {
        // Id descending as the tiebreak: UuidV7 is time-ordered, so it agrees with CreatedAt and makes
        // "most recent" total rather than dependent on Postgres's physical row order when two runs
        // share a timestamp.
        var summaryJson = await db.Set<BulkRun>()
            .Where(r => r.RunType == runType
                     && r.PeriodYear == period.Year
                     && r.PeriodMonth == period.Month)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Select(r => r.SummaryJson)
            .FirstOrDefaultAsync(ct);

        // Parsed by RunSummary, which also writes it: one type owns both sides of the property name,
        // so a rename cannot move only the writer and silently turn every comparison into a skip.
        return summaryJson is null ? null : RunSummary.ReadMoneyPathState(summaryJson);
    }

    private IRunStrategy ResolveStrategy(RunType runType) =>
        _strategies.TryGetValue(runType, out var strategy)
            ? strategy
            : throw new InvalidOperationException(
                $"No IRunStrategy registered for RunType.{runType}. " +
                $"Register a strategy implementation in OperationsModuleServiceCollectionExtensions.");
}
