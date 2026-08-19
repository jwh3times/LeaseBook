using System.Text.Json;
using LeaseBook.Modules.Capabilities.Caching;
using LeaseBook.Modules.Capabilities.Contracts;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Operations.Runs;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The freeze (ADR-028 / ADR-019 amendment): a bulk run resolves its capability set ONCE, at
/// <see cref="RunEngine.ConfirmAsync"/> entry, and every item of that run is decided under that one
/// set. A flag flipped while the run is in flight must not make item 3 disagree with item 1.
/// <para>
/// <b>Why the state moves twice.</b> Two failure modes matter and a single flip cannot tell them
/// apart. Resolving at TRANSACTION start is the wrong entry point - OrgContextMiddleware opens the
/// transaction before the endpoint handler ever runs, so a snapshot taken there predates the run confirmation.
/// Re-resolving PER ITEM loses the freeze outright. So the state here goes OFF (transaction start),
/// ON (run-confirmation entry), OFF (mid-run): only a snapshot taken exactly at run-confirmation entry answers "on" for
/// every item, and each wrong answer is distinguishable rather than a shared "false".
/// </para>
/// <para>
/// <b>Test-isolation hazard.</b> feature_flags is global - no org_id - and this assembly shares one
/// <see cref="PostgresFixture"/> through <see cref="DatabaseCollection"/>, so every flag mutation is
/// undone in a finally that also notifies. The org-scoped half needs no cleanup: each test mints its
/// own org.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RunCapabilityFreezeTests(PostgresFixture fixture)
{
    private static readonly string Capability = CapabilityCatalog.ConsolidatedStatements.Name;

    private static readonly Guid[] Targets =
    [
        Guid.Parse("aaaaaaaa-1111-7111-8111-111111111111"),
        Guid.Parse("bbbbbbbb-2222-7222-8222-222222222222"),
        Guid.Parse("cccccccc-3333-7333-8333-333333333333"),
    ];

    /// <summary>
    /// The whole point of the task. Asserted against the PERSISTED run, not only the in-memory
    /// outcome, because the persisted row is what an auditor reads.
    /// <para>
    /// <b>What moved on 2026-08-09, and why the assertion is not weaker for it.</b> Until the planning
    /// move (ADR-019 §4, amended) the capability set was a parameter of
    /// <c>IRunStrategy.ConfirmAsync</c>, and this test read it per item through a strategy double to
    /// prove item 3 agreed with item 1. Strategies are handed no capability set now — the engine
    /// resolves once and drives the loop itself — so a per-item disagreement has no mechanism left and
    /// the per-item read was measuring a hazard that no longer exists. What remains observable is the
    /// property that always mattered: the run records the set live at CONFIRM ENTRY, and the three
    /// flips below are what make each wrong answer distinguishable rather than a shared "false".
    /// The "resolved exactly once" half is pinned arithmetically in <c>RunEngineTests</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_run_records_the_capability_set_resolved_at_confirm_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = await SeedOrgAsync(ct);
        await GrantEntitlementAsync(org, ct);

        try
        {
            await using var scope = fixture.Api.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DbContext>();
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var snapshot = scope.ServiceProvider.GetRequiredService<ICapabilitySnapshot>();
            var cache = fixture.Api.Services.GetRequiredService<CapabilityCache>();

            // Prime the 30s cache with the resolved OFF set, and never notify it afterwards. That
            // makes this suite catch a second regression for free: an adapter that answered from
            // GetCachedAsync instead of ResolveDurableAsync would hand the run the stale "off" set
            // below, and a kill switch that waits out a TTL does not work during the incident it was
            // flipped for.
            (await cache.GetAsync(org, null, ct))
                .IsEnabled(CapabilityCatalog.ConsolidatedStatements)
                .ShouldBeFalse("the cache must hold a resolved 'off' set before the flip");

            // Mid-run: flip back OFF from another connection, then prove - on the run's own
            // transaction - that a fresh resolve would now disagree. Without this control a late
            // re-resolve could pass vacuously because nothing observable had changed.
            //
            // The hook fires from inside the ENGINE's posting loop, between item 1 and item 2. That is
            // where a re-resolve would now have to live, and it is later in the run confirmation than the old
            // strategy-side hook could reach.
            var posting = new HookedBatchPosting(afterFirstPost: async () =>
            {
                await WriteFlagAsync(enabled: false, ct);
                (await snapshot.ResolveDurableAsync(ct))
                    .IsEnabled(Capability)
                    .ShouldBeFalse(
                        "control: the mid-run flip must be visible to a fresh read inside the " +
                        "run's own transaction, otherwise a late re-resolve would pass anyway");
            });

            var engine = new RunEngine(
                db, [new PlanEveryTargetStrategy(Targets)], posting, TimeProvider.System,
                new RunPeriodLock(db), snapshot);

            RunResult? result = null;
            string offVersion = string.Empty;
            string onVersion = string.Empty;

            await executor.RunAsSystemAsync(
                org, "test-harness",
                async () =>
                {
                    // Transaction start: OFF (entitlement granted, no flag row, registry default).
                    var atTransactionStart = await snapshot.ResolveDurableAsync(ct);
                    atTransactionStart.IsEnabled(Capability)
                        .ShouldBeFalse("negative control - the transaction opens with the capability off");
                    offVersion = atTransactionStart.Version;

                    // Flip ON after the transaction is open but before run-confirmation entry. A snapshot
                    // taken at transaction start would answer "off" here and fail below.
                    await WriteFlagAsync(enabled: true, ct);
                    var atConfirmEntry = await snapshot.ResolveDurableAsync(ct);
                    atConfirmEntry.IsEnabled(Capability)
                        .ShouldBeTrue(
                            "READ COMMITTED: a committed flip is visible to the open transaction - " +
                            "the precondition that makes every failure mode observable");
                    onVersion = atConfirmEntry.Version;

                    // Control: the cached entry is neither expired nor invalidated, so the
                    // cache-served path still answers with the pre-flip value at this instant.
                    (await cache.GetAsync(org, null, ct))
                        .IsEnabled(CapabilityCatalog.ConsolidatedStatements)
                        .ShouldBeFalse("the cache must still be stale at the moment of the run confirmation");

                    // No echoed token: this suite is about the freeze WITHIN one run confirmation, and the
                    // flips above are exactly what the preview/confirm guard rejects. Passing a
                    // token here would make the run 409 before the freeze could be observed at all.
                    result = await engine.ConfirmAsync(
                        RunType.Rent, new RunPeriod(2026, 6), Targets,
                        expectedCapabilitiesVersion: null, acknowledgeCapabilityChange: false, ct);
                },
                ct);

            result.ShouldNotBeNull();

            // Without this the summary assertion below could pass on a run that never noticed either
            // flip: two states that hash to one token make every "which set was it" question vacuous.
            onVersion.ShouldNotBe(
                offVersion, "the on and off states must produce different version tokens");

            posting.Posts.ShouldBe(
                Targets.Length,
                "the engine must have driven every planned posting - the mid-run flip has to land " +
                "with items still to come, or it is not mid-run at all");

            var statuses = await ReadItemStatusesAsync(org, result!.RunId, ct);
            statuses.Count.ShouldBe(Targets.Length);
            statuses.ShouldAllBe(s => s == RunItemStatus.Posted);

            var summary = await ReadSummaryJsonAsync(org, result.RunId, ct);
            using var parsed = JsonDocument.Parse(summary);

            parsed.RootElement.GetProperty("capabilities").GetString().ShouldBe(
                onVersion,
                "the run must record the set captured at ConfirmAsync entry (ON): not the state at " +
                "transaction start (OFF), not the stale cached set, and not a late re-resolve (OFF)");
            parsed.RootElement.GetProperty("capabilitiesEnabled").EnumerateArray()
                .Select(e => e.GetString())
                .ShouldContain(Capability);
        }
        finally
        {
            await RemoveFlagAsync(ct);
        }
    }

    /// <summary>
    /// The committed run states which capability state it ran under. Folded into the summary BEFORE
    /// the first save: SetSummaryJson is valid only in the Added state and
    /// RevokeAppendOnly("bulk_runs") removes UPDATE entirely, so a later patch is impossible rather
    /// than merely awkward.
    /// </summary>
    [Fact]
    public async Task The_resolved_money_path_state_is_recorded_in_summary_json()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = await SeedOrgAsync(ct);
        await GrantEntitlementAsync(org, ct);

        try
        {
            await using var scope = fixture.Api.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DbContext>();
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var snapshot = scope.ServiceProvider.GetRequiredService<ICapabilitySnapshot>();

            var engine = new RunEngine(
                db, [new PlanEveryTargetStrategy(Targets)], new HookedBatchPosting(),
                TimeProvider.System, new RunPeriodLock(db), snapshot);

            await WriteFlagAsync(enabled: true, ct);

            RunResult? result = null;
            var resolvedVersion = string.Empty;
            await executor.RunAsSystemAsync(
                org, "test-harness",
                async () =>
                {
                    resolvedVersion = (await snapshot.ResolveDurableAsync(ct)).Version;
                    result = await engine.ConfirmAsync(
                        RunType.Rent, new RunPeriod(2026, 7), Targets,
                        expectedCapabilitiesVersion: null, acknowledgeCapabilityChange: false, ct);
                },
                ct);

            var summary = await ReadSummaryJsonAsync(org, result!.RunId, ct);
            using var parsed = JsonDocument.Parse(summary);
            var root = parsed.RootElement;

            root.GetProperty("posted").GetInt32().ShouldBe(Targets.Length);
            root.GetProperty("skipped").GetInt32().ShouldBe(0);
            root.GetProperty("excluded").GetInt32().ShouldBe(0);
            root.GetProperty("total").GetDecimal().ShouldBe(0m);

            root.GetProperty("capabilities").GetString().ShouldBe(
                resolvedVersion,
                "the recorded version must be the set the run actually ran under - the cross-run " +
                "consistency check parses exactly this");
            root.GetProperty("capabilitiesEnabled").EnumerateArray()
                .Select(e => e.GetString())
                .ShouldContain(Capability);
        }
        finally
        {
            await RemoveFlagAsync(ct);
        }
    }

    /// <summary>
    /// The adapter is a pass-through onto the AMBIENT transaction. Opening a scope or reaching for
    /// IPlatformScope there would call BeginTransactionAsync with one already open and throw on the
    /// money path, so the nesting is asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task The_snapshot_adapter_joins_the_ambient_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = await SeedOrgAsync(ct);

        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var snapshot = scope.ServiceProvider.GetRequiredService<ICapabilitySnapshot>();

        await executor.RunAsSystemAsync(
            org, "test-harness",
            async () =>
            {
                var before = db.Database.CurrentTransaction;
                before.ShouldNotBeNull("the ambient transaction is the precondition under test");

                var resolved = await snapshot.ResolveDurableAsync(ct);

                resolved.Version.ShouldNotBeNullOrWhiteSpace();
                db.Database.CurrentTransaction.ShouldBe(
                    before, "the adapter must join the ambient transaction, not open its own");

                // The completeness guarantee has to survive the module hop. CapabilitySet asserts it
                // resolves every capability in the registry; if the adapter projected that down to
                // enabled-names-only, an unknown name would read as a silent "off" on the money path
                // instead of throwing — the exact hazard the producing type refuses to permit.
                resolved.Values.Keys.Order(StringComparer.Ordinal).ShouldBe(
                    CapabilityCatalog.All.Select(c => c.Name).Order(StringComparer.Ordinal),
                    "the adapter must carry the complete resolved map, not the enabled subset");
            },
            ct);
    }

    /// <summary>
    /// Missing organization context faults the returned task rather than throwing synchronously - the gate was
    /// made async-throwing precisely because this adapter is its next consumer, and the difference is
    /// invisible under a bare await but load-bearing under Task.WhenAll. The call below is
    /// deliberately made without a try and before any await.
    /// </summary>
    [Fact]
    public async Task Resolving_with_no_org_context_faults_rather_than_answering_off()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var snapshot = scope.ServiceProvider.GetRequiredService<ICapabilitySnapshot>();

        var pending = snapshot.ResolveDurableAsync(ct);

        (await Should.ThrowAsync<InvalidOperationException>(async () => await pending))
            .Message.ShouldContain("organization context");
    }

    // -- helpers ---------------------------------------------------------------

    private async Task<Guid> SeedOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO orgs (id, name, created_at) VALUES (@id, 'run-capability-freeze', now())", conn);
        cmd.Parameters.AddWithValue("id", orgId);
        await cmd.ExecuteNonQueryAsync(ct);

        return orgId;
    }

    /// <summary>
    /// ConsolidatedStatements is RequiresGrant: true, so without a live grant the resolver
    /// short-circuits to false and no flag flip could ever be observed - every assertion here would
    /// pass vacuously as "off".
    /// </summary>
    private async Task GrantEntitlementAsync(Guid orgId, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var platform = scope.ServiceProvider.GetRequiredService<IPlatformScope>();
        var id = UuidV7.NewId();

        await platform.RunAsync(
            async () => await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO entitlements (id, org_id, capability, granted, effective_at, actor)
                 VALUES ({id}, {orgId}, {Capability}, true, now(), 'freeze-test')
                 """, ct),
            ct);
    }

    /// <summary>
    /// The flip, on its own connection and its own transaction so it COMMITS while the run's
    /// transaction is still open - that is the race under test.
    /// </summary>
    private async Task WriteFlagAsync(bool enabled, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await RlsProbe.SetPlatformAsync(conn, tx, ct);
        await ExecAsync(
            conn, tx,
            """
            INSERT INTO feature_flags (name, enabled, updated_at, updated_by)
            VALUES (@name, @enabled, now(), 'freeze-test')
            ON CONFLICT (name) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = EXCLUDED.updated_at
            """, ct, ("name", Capability), ("enabled", enabled));

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Restores the shared, global flag state. The delete DOES notify, so any host in this collection
    /// drops its cached set immediately rather than carrying a flipped flag into a sibling test.
    /// </summary>
    private async Task RemoveFlagAsync(CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await RlsProbe.SetPlatformAsync(conn, tx, ct);
        await ExecAsync(conn, tx, "DELETE FROM feature_flags WHERE name = @name", ct, ("name", Capability));
        await ExecAsync(
            conn, tx, $"SELECT pg_notify('{CapabilityNotifications.Channel}', @name)", ct,
            ("name", Capability));

        await tx.CommitAsync(ct);
    }

    private async Task<IReadOnlyList<RunItemStatus>> ReadItemStatusesAsync(
        Guid orgId, Guid runId, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();

        List<RunItemStatus> statuses = [];
        await executor.RunAsSystemAsync(
            orgId, "test-harness",
            async () => statuses = await db.Set<BulkRunItem>()
                .Where(i => i.RunId == runId)
                .OrderBy(i => i.TargetId)
                .Select(i => i.Status)
                .ToListAsync(ct),
            ct);

        return statuses;
    }

    private async Task<string> ReadSummaryJsonAsync(Guid orgId, Guid runId, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();

        var summary = string.Empty;
        await executor.RunAsSystemAsync(
            orgId, "test-harness",
            async () => summary = await db.Set<BulkRun>()
                .Where(r => r.Id == runId)
                .Select(r => r.SummaryJson)
                .SingleAsync(ct),
            ct);

        return summary;
    }

    private static async Task<int> ExecAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }

}

/// <summary>
/// Plans one zero-amount posting per target and decides nothing else. It is handed no capability set,
/// which is the point: after the planning move (ADR-019 §4, amended 2026-08-09) no strategy is, so
/// the freeze can only be observed where the engine records it.
/// </summary>
file sealed class PlanEveryTargetStrategy(Guid[] targets) : IRunStrategy
{
    public RunType RunType => RunType.Rent;

    public Task<StrategyPreview> PreviewAsync(RunPeriod period, CancellationToken ct)
    {
        var rows = targets
            .Select(t => new PreviewRow(
                RunTargetKind.Lease, t, $"Lease {t:N}", 0m, false, null,
                new Dictionary<string, string>()))
            .ToList();

        return Task.FromResult(new StrategyPreview(rows, []));
    }

    public Task<IReadOnlyList<RunPlanItem>> PlanAsync(
        RunPeriod period, IReadOnlyList<Guid> selectedTargetIds, CancellationToken ct)
    {
        IReadOnlyList<RunPlanItem> plan = selectedTargetIds
            .Select(RunPlanItem (id) => new PlannedPosting(
                RunTargetKind.Lease, id,
                new RentChargeIntent(
                    LeaseId: id, TenantId: id, PropertyId: id, OwnerId: id, UnitId: null,
                    Amount: 0m, Date: new DateOnly(period.Year, period.Month, 1),
                    DueDate: new DateOnly(period.Year, period.Month, 1),
                    Description: $"Freeze probe {period.Key}",
                    SourceRef: $"freeze:{period.Key}:lease={id}"),
                Amount: 0m,
                PostedDetail: new Dictionary<string, object?>(StringComparer.Ordinal),
                RefusedDetail: new Dictionary<string, object?>(StringComparer.Ordinal)))
            .ToList();

        return Task.FromResult(plan);
    }
}

/// <summary>
/// Posting that writes nothing — this suite proves the freeze, not the postings — with an optional
/// hook fired after the first one. The hook is what makes "mid-run" mean mid-run: it runs inside the
/// engine's own item loop, with items still to come.
/// </summary>
file sealed class HookedBatchPosting(Func<Task>? afterFirstPost = null) : IBatchPosting
{
    public int Posts { get; private set; }

    public async Task<PostOutcome> PostAsync(RunIntent intent, CancellationToken ct)
    {
        Posts++;

        if (Posts == 1 && afterFirstPost is not null)
        {
            await afterFirstPost();
        }

        return PostOutcome.Posted(Guid.Empty);
    }
}
