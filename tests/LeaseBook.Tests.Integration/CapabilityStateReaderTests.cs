using LeaseBook.Modules.Capabilities.Resolution;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The state reader against real rows and real RLS (ADR-028). Everything here is org-scoped —
/// entitlements and cohorts, never <c>feature_flags</c> — so each test mints its own org and leaves
/// no shared state behind for siblings in <see cref="DatabaseCollection"/>.
/// <para>
/// <b>What is deliberately NOT tested:</b> the <c>granted ASC</c> tie-break between two entitlement
/// rows at the same <c>effective_at</c>. <c>ux_entitlements_org_capability_effective_at</c> rejects
/// the second row, so the tie cannot be constructed through any role. The ordering stays fail-closed
/// as defence in depth against that index being dropped or relaxed — see
/// <see cref="CapabilityStateReader"/> and the pin in <c>SchemaGuardTests</c>.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CapabilityStateReaderTests(PostgresFixture fixture)
{
    private static readonly string Capability = CapabilityCatalog.ConsolidatedStatements.Name;

    /// <summary>
    /// Current state is the LATEST row per (org, capability), not "any grant ever existed". Without
    /// the window read a revoked paid capability would stay live forever, because the table is
    /// append-only and the original grant row never goes away.
    /// </summary>
    [Fact]
    public async Task A_later_revoke_beats_an_earlier_grant()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = await SeedOrgAsync(ct);

        await using var db = fixture.CreateContext(fixture.AppConnectionString);
        var executor = new PlatformScopedExecutor(db);
        var reader = new CapabilityStateReader(db);

        await executor.RunAsync(
            async () =>
            {
                await GrantAsync(db, org, granted: true, hoursAgo: 2, ct);
                await GrantAsync(db, org, granted: false, hoursAgo: 1, ct);

                // A cohort row is what would otherwise turn this on: ConsolidatedStatements is
                // DefaultEnabled: false with no flag row, so without it the assertion could not tell
                // "revoked" apart from "never enabled".
                await AddCohortAsync(db, org, userId: null, ct);

                var set = await reader.ReadAsync(org, null, requirePlatformScope: true, ct);
                set.IsEnabled(CapabilityCatalog.ConsolidatedStatements).ShouldBeFalse(
                    "the entitlement gate runs first and the latest row is a revoke");
            },
            ct);
    }

    /// <summary>
    /// The positive control, and the re-grant-after-revoke case the append-only model exists to keep
    /// well-defined: the newest row simply wins again.
    /// </summary>
    [Fact]
    public async Task A_regrant_after_a_revoke_re_enables_the_capability()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = await SeedOrgAsync(ct);

        await using var db = fixture.CreateContext(fixture.AppConnectionString);
        var executor = new PlatformScopedExecutor(db);
        var reader = new CapabilityStateReader(db);

        await executor.RunAsync(
            async () =>
            {
                await GrantAsync(db, org, granted: true, hoursAgo: 3, ct);
                await GrantAsync(db, org, granted: false, hoursAgo: 2, ct);
                await GrantAsync(db, org, granted: true, hoursAgo: 1, ct);
                await AddCohortAsync(db, org, userId: null, ct);

                var set = await reader.ReadAsync(org, null, requirePlatformScope: true, ct);
                set.IsEnabled(CapabilityCatalog.ConsolidatedStatements).ShouldBeTrue();
            },
            ct);
    }

    /// <summary>
    /// A user-level cohort row is a deterministic NO match where there is no authenticated user
    /// (Hangfire, the CLI, InvariantSweepRunner) — never a null-propagating maybe that lets a
    /// background job inherit one user's beta access for the whole org.
    /// </summary>
    [Fact]
    public async Task A_user_level_cohort_matches_only_that_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = await SeedOrgAsync(ct);
        var user = UuidV7.NewId();
        var otherUser = UuidV7.NewId();

        await using var db = fixture.CreateContext(fixture.AppConnectionString);
        var executor = new PlatformScopedExecutor(db);
        var reader = new CapabilityStateReader(db);

        await executor.RunAsync(
            async () =>
            {
                await GrantAsync(db, org, granted: true, hoursAgo: 1, ct);
                await AddCohortAsync(db, org, userId: user, ct);

                (await reader.ReadAsync(org, user, requirePlatformScope: true, ct))
                    .IsEnabled(CapabilityCatalog.ConsolidatedStatements)
                    .ShouldBeTrue("the cohort row names this user");

                (await reader.ReadAsync(org, otherUser, requirePlatformScope: true, ct))
                    .IsEnabled(CapabilityCatalog.ConsolidatedStatements)
                    .ShouldBeFalse("a different user in the same org is not in the cohort");

                (await reader.ReadAsync(org, null, requirePlatformScope: true, ct))
                    .IsEnabled(CapabilityCatalog.ConsolidatedStatements)
                    .ShouldBeFalse("with no authenticated user a user-level row must not match");
            },
            ct);
    }

    /// <summary>
    /// The silent-miss guard. A tenant-plane read with no org context does not raise — it returns
    /// zero rows, which resolves to "no entitlement" and therefore "off" for every paid capability,
    /// with nothing logged anywhere. Out-of-band refreshes assert the GUC so that becomes a throw,
    /// mirroring the rule that a background job with missing org context fails rather than returning
    /// empty.
    /// </summary>
    [Fact]
    public async Task Requiring_platform_scope_throws_when_the_guc_is_unset()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = await SeedOrgAsync(ct);

        await using var db = fixture.CreateContext(fixture.AppConnectionString);
        var reader = new CapabilityStateReader(db);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            async () => await reader.ReadAsync(org, null, requirePlatformScope: true, ct));

        ex.Message.ShouldContain("platform scope");
    }

    /// <summary>
    /// The reader is registry-driven, not row-driven: a capability with no row anywhere still gets a
    /// value. <c>CapabilitySet.From</c> asserts this, so a row-driven reader would throw here rather
    /// than quietly shipping a partial set.
    /// </summary>
    [Fact]
    public async Task Resolves_every_registry_capability_even_with_no_rows_at_all()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = await SeedOrgAsync(ct);

        await using var db = fixture.CreateContext(fixture.AppConnectionString);
        var executor = new PlatformScopedExecutor(db);
        var reader = new CapabilityStateReader(db);

        await executor.RunAsync(
            async () =>
            {
                var set = await reader.ReadAsync(org, null, requirePlatformScope: true, ct);

                set.Version.ShouldNotBeNullOrWhiteSpace();
                foreach (var capability in CapabilityCatalog.All)
                {
                    // No rows exist, so every answer is the registry default — and, critically,
                    // construction did not throw on a missing key.
                    set.IsEnabled(capability).ShouldBe(
                        capability.DefaultEnabled && !capability.RequiresGrant, capability.Name);
                }
            },
            ct);
    }

    /// <summary>
    /// A future-dated row is PENDING, not live (ADR-028). Both directions matter: a grant must not
    /// take effect on write, and a scheduled REVOKE must not cut a paying org off the moment it is
    /// recorded. Without the <c>effective_at &lt;= now()</c> filter the second is the expensive one.
    /// </summary>
    [Fact]
    public async Task A_future_dated_row_does_not_take_effect_yet()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = await SeedOrgAsync(ct);

        await using var db = fixture.CreateContext(fixture.AppConnectionString);
        var executor = new PlatformScopedExecutor(db);
        var reader = new CapabilityStateReader(db);

        await executor.RunAsync(
            async () =>
            {
                await GrantAsync(db, org, granted: true, hoursAgo: 1, ct);
                await AddCohortAsync(db, org, userId: null, ct);

                // Scheduled for an hour from now: the live answer must still be the grant.
                await GrantAsync(db, org, granted: false, hoursAgo: -1, ct);

                (await reader.ReadAsync(org, null, requirePlatformScope: true, ct))
                    .IsEnabled(CapabilityCatalog.ConsolidatedStatements)
                    .ShouldBeTrue("a revoke dated in the future must not revoke now");
            },
            ct);
    }

    /// <summary>
    /// The tenant-plane mirror of the platform-scope assertion, on the path Task 6 uses. An
    /// <c>app.org_id</c> that does not match the org being resolved is not an error to Postgres:
    /// <c>_org_read</c> filters both org-scoped queries to zero rows, every RequiresGrant capability
    /// resolves false, and the caller gets a plausible "not entitled" with nothing logged.
    /// </summary>
    [Fact]
    public async Task Reading_with_no_platform_scope_and_no_matching_org_context_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = await SeedOrgAsync(ct);
        var otherOrg = await SeedOrgAsync(ct);

        await using var db = fixture.CreateContext(fixture.AppConnectionString);
        var reader = new CapabilityStateReader(db);

        // No context at all.
        var bare = await Should.ThrowAsync<InvalidOperationException>(
            async () => await reader.ReadAsync(org, null, ct));
        bare.Message.ShouldContain("app.org_id");

        // Context present, but for a different org — the case a plain "is it set?" check would miss.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT set_config('app.org_id', {otherOrg.ToString()}, true)", ct);

        var mismatched = await Should.ThrowAsync<InvalidOperationException>(
            async () => await reader.ReadAsync(org, null, ct));
        mismatched.Message.ShouldContain("app.org_id");

        await tx.RollbackAsync(ct);
    }

    /// <summary>
    /// The positive control for the test above, and the shape Task 6 depends on: with the ambient
    /// org context matching, resolution works on the tenant plane with no platform escape at all.
    /// </summary>
    [Fact]
    public async Task Reads_its_own_org_on_the_tenant_plane_without_platform_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = await SeedOrgAsync(ct);

        await using var db = fixture.CreateContext(fixture.AppConnectionString);
        var reader = new CapabilityStateReader(db);

        await new PlatformScopedExecutor(db).RunAsync(
            async () =>
            {
                await GrantAsync(db, org, granted: true, hoursAgo: 1, ct);
                await AddCohortAsync(db, org, userId: null, ct);
            },
            ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT set_config('app.org_id', {org.ToString()}, true)", ct);

        var set = await reader.ReadAsync(org, null, ct);

        set.IsEnabled(CapabilityCatalog.ConsolidatedStatements).ShouldBeTrue(
            "the tenant plane may read its own entitlement and cohort rows via _org_read");

        await tx.RollbackAsync(ct);
    }

    /// <summary>
    /// The readiness path (J2). It must succeed against a deployment with no flag rows at all, which
    /// is every fresh install — a probe that fetched a row would report the seam unreachable there.
    /// </summary>
    [Fact]
    public async Task Reachability_probe_succeeds_under_platform_scope()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var db = fixture.CreateContext(fixture.AppConnectionString);
        var reader = new CapabilityStateReader(db);

        await new PlatformScopedExecutor(db).RunAsync(() => reader.ProbeReachabilityAsync(ct), ct);

        // And it is a real assertion, not a no-op: outside platform scope it refuses.
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            async () => await reader.ProbeReachabilityAsync(ct));
        ex.Message.ShouldContain("platform scope");
    }

    [Fact]
    public async Task Rejects_an_empty_org_id()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var db = fixture.CreateContext(fixture.AppConnectionString);
        var reader = new CapabilityStateReader(db);

        await Should.ThrowAsync<ArgumentException>(
            async () => await reader.ReadAsync(Guid.Empty, null, ct));
    }

    private static Task GrantAsync(
        AppDbContext db, Guid orgId, bool granted, int hoursAgo, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO entitlements (id, org_id, capability, granted, effective_at, actor)
             VALUES ({UuidV7.NewId()}, {orgId}, {Capability}, {granted},
                     now() - make_interval(hours => {hoursAgo}), 'reader-test')
             """, ct);

    private static Task AddCohortAsync(AppDbContext db, Guid orgId, Guid? userId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO capability_cohorts (id, capability, org_id, user_id, added_at, added_by)
             VALUES ({UuidV7.NewId()}, {Capability}, {orgId}, {userId}, now(), 'reader-test')
             """, ct);

    private async Task<Guid> SeedOrgAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO orgs (id, name, created_at) VALUES (@id, 'capability-reader', now())", conn);
        cmd.Parameters.AddWithValue("id", orgId);
        await cmd.ExecuteNonQueryAsync(ct);

        return orgId;
    }
}
