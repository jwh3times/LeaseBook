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
            result = await engine.ConfirmAsync(
                RunType.Rent, new RunPeriod(2026, 6), [_t1, _t2],
                expectedCapabilitiesVersion: null, acknowledgeCapabilityChange: false, ct);
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

    /// <summary>
    /// The preview hands the operator the token its confirm must echo. Without this the token would
    /// have to come from somewhere else, and the two sides could diverge in derivation.
    /// </summary>
    [Fact]
    public async Task Preview_stamps_the_resolved_capability_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = await OrgScope.CreateAsync(fixture, ct);
        await using var _ = scope;

        var snapshot = new CountingCapabilitySnapshot("v1.first");
        var engine = BuildEngine(scope, new NoOpStrategy(targets: [_t1]), snapshot);

        RunPreview? preview = null;
        await scope.RunAsync(async () =>
        {
            preview = await engine.PreviewAsync(RunType.Rent, new RunPeriod(2026, 6), ct);
        }, ct);

        preview.ShouldNotBeNull();
        preview!.CapabilitiesVersion.ShouldBe("v1.first");
    }

    /// <summary>
    /// The freeze, restated as an arithmetic fact: adding the preview/confirm comparison must not add
    /// a resolve. A second read inside <c>ConfirmAsync</c> would compare a value against itself and
    /// leave the run deciding items under a set nobody pinned.
    /// </summary>
    [Fact]
    public async Task Confirm_resolves_the_capability_set_exactly_once_even_with_a_token_to_compare()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = await OrgScope.CreateAsync(fixture, ct);
        await using var _ = scope;

        var snapshot = new CountingCapabilitySnapshot("v1.steady");
        var engine = BuildEngine(scope, new NoOpStrategy(targets: [_t1, _t2]), snapshot);

        await scope.RunAsync(async () =>
        {
            await engine.ConfirmAsync(
                RunType.Rent, new RunPeriod(2026, 6), [_t1, _t2], "v1.steady",
                acknowledgeCapabilityChange: false, ct);
        }, ct);

        snapshot.Resolves.ShouldBe(1, "one confirm, one resolve — the token is compared, not re-read");
    }

    [Fact]
    public async Task Confirm_acquires_the_period_lock_before_resolving_capabilities()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = await OrgScope.CreateAsync(fixture, ct);
        await using var _ = scope;
        var period = new RunPeriod(2026, 6);
        var periodLock = new RecordingRunPeriodLock();
        var snapshot = new LockAssertingCapabilitySnapshot(periodLock);
        var engine = BuildEngine(scope, new NoOpStrategy(targets: [_t1]), snapshot, periodLock);

        await scope.RunAsync(
            async () => await engine.ConfirmAsync(
                RunType.Rent, period, [_t1], expectedCapabilitiesVersion: null,
                acknowledgeCapabilityChange: false, ct),
            ct);

        periodLock.Acquisitions.ShouldBe([(RunType.Rent, period)]);
        snapshot.Resolves.ShouldBe(1);
    }

    /// <summary>
    /// A mismatch rejects, and rejects BEFORE the strategy runs. The strategy-untouched assertion is
    /// the load-bearing half: a guard that threw after the posting loop would leave a half-posted run
    /// behind, which is worse than no guard.
    /// </summary>
    [Fact]
    public async Task Confirm_with_a_stale_token_throws_before_the_strategy_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var scope = await OrgScope.CreateAsync(fixture, ct);
        await using var _ = scope;

        var snapshot = new CountingCapabilitySnapshot("v1.current");
        var strategy = new NoOpStrategy(targets: [_t1, _t2]);
        var engine = BuildEngine(scope, strategy, snapshot);

        CapabilitiesChangedException? thrown = null;
        await scope.RunAsync(async () =>
        {
            thrown = await Should.ThrowAsync<CapabilitiesChangedException>(
                async () => await engine.ConfirmAsync(
                    RunType.Rent, new RunPeriod(2026, 6), [_t1, _t2], "v1.stale",
                    acknowledgeCapabilityChange: false, ct));
        }, ct);

        thrown.ShouldNotBeNull();
        strategy.Confirms.ShouldBe(0, "a rejected confirm must post nothing at all");

        // Neither opaque digest may reach the operator — ADR-025's error-content rule.
        thrown!.Message.ShouldNotContain("v1.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static RunEngine BuildEngine(
        OrgScope scope,
        IRunStrategy strategy,
        ICapabilitySnapshot? snapshot = null,
        IRunPeriodLock? periodLock = null)
    {
        var strategies = new[] { strategy };
        var posting = new NoOpBatchPosting();
        return new RunEngine(
            scope.Db, strategies, posting, TimeProvider.System,
            periodLock ?? new RunPeriodLock(scope.Db),
            snapshot ?? new StubCapabilitySnapshot());
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

    /// <summary>How many times the engine reached the strategy — 0 proves a rejection came first.</summary>
    public int Confirms { get; private set; }

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
        BulkRun run, IReadOnlyList<Guid> selectedTargetIds, IBatchPosting posting,
        RunCapabilities capabilities, CancellationToken ct)
    {
        Confirms++;
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
    public Task<PostOutcome> PostAsync(RunIntent intent, CancellationToken ct) =>
        Task.FromResult(PostOutcome.Posted(Guid.Empty));
}

/// <summary>
/// A stub <see cref="ICapabilitySnapshot"/>. This project has no host and no Capabilities module
/// reference by design — the port lives in Operations precisely so the module is testable without
/// one. The freeze itself is proven against the real gate in
/// <c>LeaseBook.Tests.Integration.RunCapabilityFreezeTests</c>.
/// </summary>
file sealed class StubCapabilitySnapshot : ICapabilitySnapshot
{
    public Task<RunCapabilities> ResolveDurableAsync(CancellationToken ct) =>
        // Deliberately empty: this project cannot see the registry (Operations must not reference the
        // Capabilities module), and RunCapabilities.IsEnabled throws on an unknown name, so a stub
        // that pretended to resolve anything would be lying about completeness. NoOpStrategy asks it
        // nothing.
        Task.FromResult(new RunCapabilities(
            new Dictionary<string, bool>(StringComparer.Ordinal), "stub",
            new HashSet<string>(StringComparer.Ordinal)));
}

/// <summary>
/// The same stub, but it counts. The preview/confirm guard is the kind of change that quietly turns
/// one resolve into two — the comparison needs a "current" value, and the obvious way to get one is
/// to read it again — so the count is asserted rather than reasoned about.
/// </summary>
file sealed class CountingCapabilitySnapshot(string version) : ICapabilitySnapshot
{
    public int Resolves { get; private set; }

    public Task<RunCapabilities> ResolveDurableAsync(CancellationToken ct)
    {
        Resolves++;
        return Task.FromResult(new RunCapabilities(
            new Dictionary<string, bool>(StringComparer.Ordinal), version,
            new HashSet<string>(StringComparer.Ordinal)));
    }
}

file sealed class RecordingRunPeriodLock : IRunPeriodLock
{
    public List<(RunType RunType, RunPeriod Period)> Acquisitions { get; } = [];

    public Task AcquireAsync(RunType runType, RunPeriod period, CancellationToken ct)
    {
        Acquisitions.Add((runType, period));
        return Task.CompletedTask;
    }
}

file sealed class LockAssertingCapabilitySnapshot(RecordingRunPeriodLock periodLock) : ICapabilitySnapshot
{
    public int Resolves { get; private set; }

    public Task<RunCapabilities> ResolveDurableAsync(CancellationToken ct)
    {
        periodLock.Acquisitions.Count.ShouldBe(1, "the period lock must be held before durable resolution");
        Resolves++;
        return Task.FromResult(new RunCapabilities(
            new Dictionary<string, bool>(StringComparer.Ordinal), "locked",
            new HashSet<string>(StringComparer.Ordinal)));
    }
}
