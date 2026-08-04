using System.Text.Json;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.SharedKernel.Observability;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// The shared run pipeline (ADR-019 / M6 WP-1). Resolves the right <see cref="IRunStrategy"/> by
/// <see cref="RunType"/>, delegates preview and confirm work to it, persists the <see cref="BulkRun"/>
/// header + <see cref="BulkRunItem"/> rows, and returns a <see cref="RunResult"/>.
/// <para>
/// <b>Transaction model:</b> <c>ConfirmAsync</c> does NOT open a new transaction. It must be called
/// inside the ambient org-scoped transaction (set up by the request middleware or
/// <c>OrgScopedExecutor</c>). The strategy posts through <see cref="IBatchPosting"/> under that same
/// transaction; all writes are committed together. Per-item posting exceptions
/// (<c>DuplicateSourceRefException</c>, period-lock) are expected to be caught inside the strategy's
/// <c>ConfirmAsync</c> and tagged as <c>Skipped</c> / <c>Excluded</c>; no unhandled posting exception
/// should escape.
/// </para>
/// <para>
/// <b>Capability freeze:</b> <c>ConfirmAsync</c> resolves the capability set exactly once, at its own
/// entry, and hands it to the strategy as a parameter. One run therefore decides every item under one
/// set even if an operator flips a flag mid-run. The freeze holds today partly because confirm runs
/// inside a single request transaction; it is carried by the signature so that a future chunked
/// confirm (ADR-019's revisit trigger) cannot lose it silently at a chunk boundary. A capability may
/// gate whether a posting path is reachable and nothing else — it must never become an input to what
/// an event posts.
/// </para>
/// <para>
/// <b>The preview → confirm window.</b> The freeze above makes one confirm internally consistent; it
/// says nothing about the gap before it. <c>PreviewAsync</c> stamps the resolved version onto the
/// <see cref="RunPreview"/>, the confirm echoes it back, and <c>ConfirmAsync</c> compares it against
/// the set it resolves itself — optimistic concurrency, the same shape as an ETag. The operator
/// selected target <i>ids</i>, but the <i>amounts</i> they approved were the preview's, so a set that
/// moved in between makes the confirm a different operation from the one they authorized. Nothing
/// about the preview is persisted to support this: the token is derived from state, not stored.
/// </para>
/// <para>
/// <b>The cross-run window.</b> The two guards above make ONE run consistent. A period is
/// routinely built by more than one run, because the designed recovery path IS a re-run:
/// <c>source_ref</c> uniqueness lands already-posted items as <c>Skipped</c> (ADR-019 §2). So run
/// 1 confirms a selection while a money-path capability is off, the flag flips, and run 2 confirms
/// the remainder while it is on — both internally consistent, the period not. <c>ConfirmAsync</c>
/// therefore reads the money-path state recorded by the most recent prior run for the same
/// <c>(org, run type, period)</c> and rejects a confirm that would disagree with it, unless the
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

        // Same port, same derivation, as the confirm below. Deliberately NOT the cached member: a
        // token served from a 30-second cache would disagree with the confirm's durable read for up
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
    /// When a prior run for this period recorded a different money-path state, true lets the confirm
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
        // taken there would predate the confirm the operator actually asked for.
        var capabilities = await capabilitySnapshot.ResolveDurableAsync(ct);
        activity?.SetTag("capabilities_version", capabilities.Version);

        // Close the preview → confirm window, against the ONE set resolved above. No second resolve:
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
        // method (RunEngineTests pins that count at one per confirm). bulk_runs is Operations' own
        // table and the query runs on the ambient RLS transaction, so it sees this org's runs and no
        // other org's without a predicate this method could forget.
        var moneyPathState = capabilities.MoneyPathState();
        var priorMoneyPathState = await ReadPriorMoneyPathStateAsync(runType, period, ct);
        var overrodePriorState =
            priorMoneyPathState is not null &&
            !priorMoneyPathState.SequenceEqual(moneyPathState, StringComparer.Ordinal);

        if (overrodePriorState && !acknowledgeCapabilityChange)
        {
            throw new CapabilitiesChangedSincePriorRunException();
        }

        activity?.SetTag("capability_change_acknowledged", overrodePriorState);

        // Let the strategy do its work — posting under the ambient transaction, and under this one
        // frozen set. Passed explicitly rather than looked up: see IRunStrategy.ConfirmAsync.
        var items = await strategy.ConfirmAsync(run, selectedTargetIds, posting, capabilities, ct);

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
        var summary = new
        {
            posted,
            skipped,
            excluded,
            total,
            capabilities = capabilities.Version,
            capabilitiesEnabled = capabilities.EnabledNames(),

            // What the NEXT run for this period compares itself against. Written by every run, so
            // the chain is unbroken from here on; a run committed before this field existed is
            // skipped rather than assumed — see ReadPriorMoneyPathStateAsync.
            capabilitiesMoneyPath = moneyPathState,

            // Always present, so "the override was not used" is a recorded fact rather than the
            // absence of one; and with it the state that was overridden, so an auditor can see what
            // the two halves of the period were computed under without replaying run history.
            capabilityChangeAcknowledged = overrodePriorState,
            capabilityChangeFrom = overrodePriorState ? priorMoneyPathState : null,
        };
        run.SetSummaryJson(JsonSerializer.Serialize(summary));

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

        if (summaryJson is null)
        {
            return null;
        }

        using var parsed = JsonDocument.Parse(summaryJson);
        if (!parsed.RootElement.TryGetProperty("capabilitiesMoneyPath", out var recorded) ||
            recorded.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return recorded.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
    }

    private IRunStrategy ResolveStrategy(RunType runType) =>
        _strategies.TryGetValue(runType, out var strategy)
            ? strategy
            : throw new InvalidOperationException(
                $"No IRunStrategy registered for RunType.{runType}. " +
                $"Register a strategy implementation in OperationsModuleServiceCollectionExtensions.");
}
