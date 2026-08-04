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
    /// <exception cref="CapabilitiesChangedException">
    /// <paramref name="expectedCapabilitiesVersion"/> is non-null and differs from the set resolved
    /// at entry.
    /// </exception>
    public async Task<RunResult> ConfirmAsync(
        RunType runType,
        RunPeriod period,
        IReadOnlyList<Guid> selectedTargetIds,
        string? expectedCapabilitiesVersion,
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

    private IRunStrategy ResolveStrategy(RunType runType) =>
        _strategies.TryGetValue(runType, out var strategy)
            ? strategy
            : throw new InvalidOperationException(
                $"No IRunStrategy registered for RunType.{runType}. " +
                $"Register a strategy implementation in OperationsModuleServiceCollectionExtensions.");
}
