using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Operations.Runs;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LeaseBook.Tests.Operations;

/// <summary>
/// Integration tests for <see cref="RunEngine"/> — the shared run-pipeline. Uses a Testcontainers
/// Postgres instance (via <see cref="PostgresFixture"/>) with the full EF migration applied so
/// <c>bulk_runs</c>/<c>bulk_run_items</c> actually exist.
///
/// A <see cref="NoOpStrategy"/> stands in for real run strategies (WP-2/3/4) so the pipeline is
/// proven end-to-end without any accounting posting logic.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RunEngineTests(PostgresFixture fixture)
{
    private static readonly Guid _t1 = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid _t2 = Guid.Parse("22222222-2222-7222-8222-222222222222");

    [Fact]
    public async Task Confirm_writes_one_run_with_one_item_per_selected_target_and_returns_counts()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = await OrgScope.CreateAsync(fixture, ct);
        await using var _ = scope;

        var engine = BuildEngine(scope, new NoOpStrategy(targets: [_t1, _t2]));

        // Act — capture result via closure (OrgScope.RunAsync is fire-and-return)
        RunResult? result = null;
        await scope.RunAsync(async () =>
        {
            result = await engine.ConfirmAsync(RunType.Rent, new RunPeriod(2026, 6), [_t1, _t2], ct);
        }, ct);

        // Assert counts returned
        result.ShouldNotBeNull();
        result!.RunId.ShouldNotBe(Guid.Empty);
        result.Posted.ShouldBe(2);
        result.Skipped.ShouldBe(0);
        result.Excluded.ShouldBe(0);
        result.Total.ShouldBe(0m);   // NoOp returns zero-amount items

        // Assert DB rows — in a fresh RunAsync to use its own transaction/RLS context
        int runCount = 0;
        int itemCount = 0;
        await scope.RunAsync(async () =>
        {
            runCount = await scope.Db.Set<BulkRun>().CountAsync(ct);
            itemCount = await scope.Db.Set<BulkRunItem>()
                .Where(i => i.RunId == result.RunId)
                .CountAsync(ct);
        }, ct);

        runCount.ShouldBe(1);
        itemCount.ShouldBe(2);
    }

    [Fact]
    public async Task Preview_delegates_to_strategy_and_returns_preview_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = await OrgScope.CreateAsync(fixture, ct);
        await using var _ = scope;

        var engine = BuildEngine(scope, new NoOpStrategy(targets: [_t1, _t2]));

        RunPreview? preview = null;
        await scope.RunAsync(async () =>
        {
            preview = await engine.PreviewAsync(RunType.Rent, new RunPeriod(2026, 6), ct);
        }, ct);

        preview.ShouldNotBeNull();
        preview!.RunType.ShouldBe(RunType.Rent);
        preview.Period.Year.ShouldBe(2026);
        preview.Period.Month.ShouldBe(6);
        preview.Rows.Count.ShouldBe(2);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static RunEngine BuildEngine(OrgScope scope, IRunStrategy strategy)
    {
        var strategies = new[] { strategy };
        var posting = new NoOpBatchPosting();
        return new RunEngine(scope.Db, strategies, posting, TimeProvider.System);
    }
}

// ── test stubs ────────────────────────────────────────────────────────────────

/// <summary>
/// A no-op <see cref="IRunStrategy"/> that previews <paramref name="targets"/> with zero amounts
/// and confirms them all as <see cref="RunItemStatus.Posted"/>. No posting actually occurs.
/// </summary>
file sealed class NoOpStrategy(Guid[] targets) : IRunStrategy
{
    public RunType RunType => RunType.Rent;

    public Task<RunPreview> PreviewAsync(RunPeriod period, CancellationToken ct)
    {
        var rows = targets
            .Select(t => new PreviewRow(
                TargetKind: RunTargetKind.Lease,
                TargetId: t,
                Label: $"Lease {t:N}",
                Amount: 0m,
                AlreadyDone: false,
                ExcludedReason: null,
                Detail: new Dictionary<string, string>()))
            .ToList();

        return Task.FromResult(new RunPreview(RunType.Rent, period, rows, []));
    }

    public Task<IReadOnlyList<BulkRunItem>> ConfirmAsync(
        BulkRun run, IReadOnlyList<Guid> selectedTargetIds, IBatchPosting posting, CancellationToken ct)
    {
        IReadOnlyList<BulkRunItem> items = selectedTargetIds
            .Select(id => BulkRunItem.Create(run.Id, RunTargetKind.Lease, id, RunItemStatus.Posted, 0m, null, run.CreatedAt))
            .ToList();
        return Task.FromResult(items);
    }
}

/// <summary>
/// A no-op <see cref="IBatchPosting"/> stub — returns empty maps; used only to satisfy the
/// engine's constructor since the <see cref="NoOpStrategy"/> never calls it.
/// </summary>
file sealed class NoOpBatchPosting : IBatchPosting
{
    public Task<IReadOnlyDictionary<Guid, Guid>> PostRentChargesAsync(
        IReadOnlyList<RentChargeIntent> intents, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(new Dictionary<Guid, Guid>());

    public Task<IReadOnlyDictionary<Guid, Guid>> PostLateFeesAsync(
        IReadOnlyList<LateFeeIntent> intents, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(new Dictionary<Guid, Guid>());

    public Task<IReadOnlyDictionary<Guid, DisbursementPostingResult>> PostDisbursementsAsync(
        IReadOnlyList<DisbursementIntent> intents, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, DisbursementPostingResult>>(
            new Dictionary<Guid, DisbursementPostingResult>());
}
