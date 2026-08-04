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
    /// entirely to the strategy; no mutations occur.
    /// </summary>
    public Task<RunPreview> PreviewAsync(RunType runType, RunPeriod period, CancellationToken ct)
    {
        var strategy = ResolveStrategy(runType);
        return strategy.PreviewAsync(period, ct);
    }

    /// <summary>
    /// Confirms the run for the given <paramref name="selectedTargetIds"/>: calls the strategy's
    /// <c>ConfirmAsync</c>, persists the <see cref="BulkRun"/> header + <see cref="BulkRunItem"/>
    /// rows, emits a telemetry span, and returns a <see cref="RunResult"/>.
    /// Must be called inside the ambient org-scoped transaction.
    /// </summary>
    public async Task<RunResult> ConfirmAsync(
        RunType runType,
        RunPeriod period,
        IReadOnlyList<Guid> selectedTargetIds,
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
            capabilitiesEnabled = capabilities.Enabled.Order(StringComparer.Ordinal).ToArray(),
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
