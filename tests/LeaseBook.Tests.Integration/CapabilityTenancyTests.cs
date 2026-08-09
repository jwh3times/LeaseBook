using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The tenancy contract for the platform tables (ADR-028), and the hard gate for the whole
/// capability seam. <c>entitlements</c> and <c>capability_cohorts</c> carry <c>org_id</c> and ARE
/// RLS'd under FORCE; the platform plane is the single deliberate escape. These tests are the reason
/// the design rejected a no-RLS "global-class" shape — under that shape
/// <see cref="App_role_in_org_a_cannot_see_org_b_entitlement"/> would fail.
/// <para>
/// Everything here drives raw SQL on the RLS-subject <b>app role</b> (pitfall E2), except the two
/// <see cref="PlatformScopedExecutor"/> tests, which are about the executor itself. Org ids are
/// freshly minted per test so rows left by siblings in the shared container cannot perturb a count.
/// Four entitlement tests deliberately count the table <i>unfiltered</i> — that is strictly stronger,
/// because RLS itself is then what has to reduce the whole shared container to the expected number.
/// </para>
/// <para>
/// The escape is TWO policies per table, not one <c>FOR ALL</c> (see
/// <c>Rls.EnableOrgRlsWithPlatformEscape</c>), so tenant-plane writes fail at deliberately different
/// layers, and the tests below assert the layer, not just "it failed":
/// <list type="bullet">
/// <item>INSERT → 42501 <i>new row violates row-level security policy</i> (the write policy's WITH CHECK).</item>
/// <item><c>entitlements</c> UPDATE/DELETE → 42501 <i>permission denied for table</i>. The append-only
/// REVOKE is a table-level ACL, checked at query-init strictly before any policy is consulted, so the
/// grant fires first and RLS never runs.</item>
/// <item><c>capability_cohorts</c> UPDATE/DELETE → <b>no exception</b>: cohort membership is mutable, so
/// the grants are retained by design, the statement passes the ACL and is then filtered to ZERO rows by
/// the write policy's USING. Asserting only "did not throw" here would pass against a completely broken
/// policy, so these tests assert the affected-row count and that the row is bit-for-bit unchanged.</item>
/// </list>
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CapabilityTenancyTests(PostgresFixture fixture)
{
    private const string Capability = "consolidated-statements";

    // ── The gate ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// If this fails, the design's central tenancy claim is false: platform data ABOUT an org would
    /// be readable by a different org. Nothing downstream is salvageable by patching a query.
    /// </summary>
    [Fact]
    public async Task App_role_in_org_a_cannot_see_org_b_entitlement()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();
        var orgB = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA, orgB], ct);
        await GrantEntitlementAsPlatformAsync(conn, orgB, Capability, ct);

        var visible = await CountAsync(conn, "SELECT count(*) FROM entitlements", orgId: orgA, platform: false, ct);

        visible.ShouldBe(0, "org A must not see org B's entitlement — RLS is the boundary");
    }

    /// <summary>
    /// The positive control for the gate. Without it, an over-correction that denied ALL tenant reads
    /// (say, dropping the <c>_org_read</c> policy) would leave every other test in this file green.
    /// </summary>
    [Fact]
    public async Task App_role_reads_its_own_entitlement_under_its_own_org_context()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();
        var orgB = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA, orgB], ct);
        await GrantEntitlementAsPlatformAsync(conn, orgA, Capability, ct);
        await GrantEntitlementAsPlatformAsync(conn, orgB, Capability, ct);

        var visible = await CountAsync(conn, "SELECT count(*) FROM entitlements", orgId: orgA, platform: false, ct);

        visible.ShouldBe(1, "a tenant must still be able to read its OWN entitlement rows");
    }

    // ── Reads ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Platform_scope_sees_entitlements_across_orgs()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();
        var orgB = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA, orgB], ct);
        await GrantEntitlementAsPlatformAsync(conn, orgA, Capability, ct);
        await GrantEntitlementAsPlatformAsync(conn, orgB, Capability, ct);

        var visible = await CountAsync(
            conn,
            "SELECT count(*) FROM entitlements WHERE org_id IN (@org, @other)",
            orgId: null, platform: true, ct,
            ("org", orgA), ("other", orgB));

        visible.ShouldBe(2);
    }

    [Fact]
    public async Task With_no_context_at_all_entitlements_are_invisible()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA], ct);
        await GrantEntitlementAsPlatformAsync(conn, orgA, Capability, ct);

        var visible = await CountAsync(conn, "SELECT count(*) FROM entitlements", orgId: null, platform: false, ct);

        visible.ShouldBe(0, "missing context must fail closed — a forgotten platform scope returns " +
                            "zero rows, never every org's rows");
    }

    /// <summary>
    /// The escape is scoped to the four platform tables. Ordinary org-scoped tables carry the plain
    /// <c>EnableOrgRls</c> policy, which never mentions <c>app.platform</c>, so opening platform scope
    /// must not turn into a general cross-tenant bypass.
    /// </summary>
    [Fact]
    public async Task Platform_scope_does_not_widen_ordinary_org_scoped_tables()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();
        var orgB = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA, orgB], ct);
        await SeedAuditEventAsync(conn, orgA, ct);
        await SeedAuditEventAsync(conn, orgB, ct);

        // Platform scope AND org A's context: still only org A's audit row.
        var underBoth = await CountAsync(
            conn,
            "SELECT count(*) FROM audit_events WHERE org_id IN (@org, @other)",
            orgId: orgA, platform: true, ct,
            ("org", orgA), ("other", orgB));
        underBoth.ShouldBe(1, "app.platform must not widen audit_events beyond the active org");

        // Platform scope alone: audit_events has no platform escape, so nothing is visible.
        var underPlatformOnly = await CountAsync(
            conn,
            "SELECT count(*) FROM audit_events WHERE org_id IN (@org, @other)",
            orgId: null, platform: true, ct,
            ("org", orgA), ("other", orgB));
        underPlatformOnly.ShouldBe(0, "app.platform is an escape for four named tables, not a bypass");
    }

    /// <summary>
    /// FORCE ROW LEVEL SECURITY binds the table owner too, and no role in this system has BYPASSRLS or
    /// SUPERUSER. This is the constraint any future seed or backfill has to respect: touching these
    /// tables means opening platform scope first, whichever role you are.
    /// </summary>
    [Fact]
    public async Task Migrator_role_is_also_bound_by_force_row_level_security()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();

        await using var appConn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(appConn, [orgA], ct);
        await GrantEntitlementAsPlatformAsync(appConn, orgA, Capability, ct);

        await using var migratorConn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await migratorConn.OpenAsync(ct);

        var blind = await CountAsync(
            migratorConn, "SELECT count(*) FROM entitlements WHERE org_id = @org",
            orgId: null, platform: false, ct, ("org", orgA));
        blind.ShouldBe(0, "the schema owner is FORCEd under RLS like everyone else");

        var scoped = await CountAsync(
            migratorConn, "SELECT count(*) FROM entitlements WHERE org_id = @org",
            orgId: null, platform: true, ct, ("org", orgA));
        scoped.ShouldBe(1);
    }

    // ── Writes: entitlements (append-only, platform-written) ────────────────────────────────────

    /// <summary>
    /// The self-grant attack the two-policy shape exists to stop: a tenant inserting its own
    /// <c>granted = true</c> row. The row's org matches the session's org, so an org-equality WITH CHECK
    /// would have allowed it. Only the platform write policy applies to INSERT, so it is rejected.
    /// </summary>
    [Fact]
    public async Task Tenant_plane_cannot_self_grant_an_entitlement()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA], ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        await SetContextAsync(conn, tx, orgId: orgA, platform: false, ct);

        var ex = await Should.ThrowAsync<PostgresException>(async () =>
            await ExecAsync(conn, tx, EntitlementInsertSql, ct,
                ("id", UuidV7.NewId()), ("org", orgA), ("cap", Capability), ("actor", "tenant")));

        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        // The write policy's WITH CHECK is what must stop this — not the grant, not a constraint.
        ex.MessageText.ShouldContain("new row violates row-level security policy");
    }

    /// <summary>
    /// Append-only, enforced by the ACL rather than by a policy: the REVOKE is checked at query-init,
    /// strictly before RLS quals are evaluated, so the error is "permission denied for table" and no
    /// policy ever runs. Distinct failure text from the INSERT case above, on purpose.
    /// </summary>
    [Fact]
    public async Task Tenant_plane_update_of_entitlements_is_denied_by_the_revoked_grant()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA], ct);
        await GrantEntitlementAsPlatformAsync(conn, orgA, Capability, ct);

        var update = await ShouldFailAsync(
            conn, orgId: orgA, platform: false, "UPDATE entitlements SET granted = true", ct);
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        update.MessageText.ShouldContain("permission denied");

        var delete = await ShouldFailAsync(
            conn, orgId: orgA, platform: false, "DELETE FROM entitlements", ct);
        delete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        delete.MessageText.ShouldContain("permission denied");
    }

    /// <summary>Append-only outranks the escape: platform scope does not restore UPDATE/DELETE.</summary>
    [Fact]
    public async Task Platform_scope_still_cannot_update_or_delete_entitlements()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA], ct);
        await GrantEntitlementAsPlatformAsync(conn, orgA, Capability, ct);

        var update = await ShouldFailAsync(
            conn, orgId: null, platform: true, "UPDATE entitlements SET granted = false", ct);
        update.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        update.MessageText.ShouldContain("permission denied");

        var delete = await ShouldFailAsync(
            conn, orgId: null, platform: true, "DELETE FROM entitlements", ct);
        delete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        delete.MessageText.ShouldContain("permission denied");
    }

    // ── Writes: capability_cohorts (mutable, platform-written) ──────────────────────────────────

    [Fact]
    public async Task Tenant_plane_cannot_add_itself_to_a_capability_cohort()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA], ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        await SetContextAsync(conn, tx, orgId: orgA, platform: false, ct);

        var ex = await Should.ThrowAsync<PostgresException>(async () =>
            await ExecAsync(conn, tx, CohortInsertSql, ct,
                ("id", UuidV7.NewId()), ("org", orgA), ("cap", Capability), ("by", "tenant")));

        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        // Same layer as the entitlements INSERT above: the write policy's WITH CHECK, not the grant.
        ex.MessageText.ShouldContain("new row violates row-level security policy");
    }

    /// <summary>
    /// UPDATE/DELETE grants are retained here on purpose (cohort membership is mutable), so the ACL
    /// passes and the write policy's USING is what filters. That means <b>no exception</b> — which is
    /// exactly why "did not throw" would be a worthless assertion. The row count and the row's
    /// contents are the real evidence.
    /// </summary>
    [Fact]
    public async Task Tenant_plane_update_and_delete_of_capability_cohorts_affect_zero_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA], ct);
        var cohortId = await AddCohortAsPlatformAsync(conn, orgA, Capability, addedBy: "platform", ct);

        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await SetContextAsync(conn, tx, orgId: orgA, platform: false, ct);

            var updated = await ExecAsync(conn, tx,
                "UPDATE capability_cohorts SET added_by = 'tampered', capability = 'everything'", ct);
            updated.ShouldBe(0, "the write policy must filter the tenant plane out of every row");

            var deleted = await ExecAsync(conn, tx, "DELETE FROM capability_cohorts", ct);
            deleted.ShouldBe(0, "the write policy must filter the tenant plane out of every row");

            await tx.CommitAsync(ct);
        }

        // The row survived both statements untouched — proof the zero counts were not an artifact of
        // a rolled-back transaction or of the row having quietly vanished.
        await using var check = await conn.BeginTransactionAsync(ct);
        await SetContextAsync(conn, check, orgId: null, platform: true, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT added_by FROM capability_cohorts WHERE id = @id", conn, check);
        cmd.Parameters.AddWithValue("id", cohortId);
        (await cmd.ExecuteScalarAsync(ct)).ShouldBe("platform");
    }

    /// <summary>
    /// The positive control for the test above. <c>capability_cohorts</c> deliberately keeps its
    /// UPDATE/DELETE grants because membership is mutable — an over-correction that added
    /// <c>RevokeAppendOnly</c> to it would make every "affects zero rows" assertion pass for entirely
    /// the wrong reason (42501 from the ACL instead of a policy filter) while breaking cohort
    /// management for good.
    /// </summary>
    [Fact]
    public async Task Platform_scope_can_update_and_delete_a_capability_cohort()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA], ct);
        var cohortId = await AddCohortAsPlatformAsync(conn, orgA, Capability, addedBy: "platform", ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        await SetContextAsync(conn, tx, orgId: null, platform: true, ct);

        var updated = await ExecAsync(conn, tx,
            "UPDATE capability_cohorts SET added_by = 'ops' WHERE id = @id", ct, ("id", cohortId));
        updated.ShouldBe(1, "cohort membership is mutable from the platform plane by design");

        var deleted = await ExecAsync(conn, tx,
            "DELETE FROM capability_cohorts WHERE id = @id", ct, ("id", cohortId));
        deleted.ShouldBe(1, "removing an org from a rollout cohort is a supported operation");

        await tx.CommitAsync(ct);
    }

    [Fact]
    public async Task App_role_reads_its_own_cohort_row_but_not_another_orgs()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();
        var orgB = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA, orgB], ct);
        await AddCohortAsPlatformAsync(conn, orgA, Capability, addedBy: "platform", ct);
        await AddCohortAsPlatformAsync(conn, orgB, Capability, addedBy: "platform", ct);

        var mine = await CountAsync(
            conn, "SELECT count(*) FROM capability_cohorts WHERE org_id IN (@org, @other)",
            orgId: orgA, platform: false, ct, ("org", orgA), ("other", orgB));

        mine.ShouldBe(1, "a tenant sees its own cohort row and only its own");
    }

    // ── platform_audit_events (platform-only) and feature_flags (tenant-readable) ────────────────

    [Fact]
    public async Task Platform_audit_events_are_invisible_inside_a_tenant_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA], ct);

        await using (var seed = await conn.BeginTransactionAsync(ct))
        {
            await SetContextAsync(conn, seed, orgId: null, platform: true, ct);
            await ExecAsync(conn, seed,
                "INSERT INTO platform_audit_events (id, occurred_at, actor, action, org_id, detail_json) " +
                "VALUES (@id, now(), 'test', 'entitlement.grant', @org, '{}')", ct,
                ("id", UuidV7.NewId()), ("org", orgA));
            await seed.CommitAsync(ct);
        }

        // Even with the row's OWN org in context — the platform-only policy has no org half at all.
        var underTenant = await CountAsync(
            conn, "SELECT count(*) FROM platform_audit_events WHERE org_id = @org",
            orgId: orgA, platform: false, ct, ("org", orgA));
        underTenant.ShouldBe(0);

        var underPlatform = await CountAsync(
            conn, "SELECT count(*) FROM platform_audit_events WHERE org_id = @org",
            orgId: null, platform: true, ct, ("org", orgA));
        underPlatform.ShouldBe(1);
    }

    /// <summary>
    /// The platform audit trail is append-only in BOTH planes — the escape does not restore
    /// UPDATE/DELETE any more than it does on <c>entitlements</c>. A record of who granted what to
    /// whom is worth nothing if the plane that writes it can also rewrite it.
    /// </summary>
    [Fact]
    public async Task Platform_audit_events_are_append_only_in_both_planes()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA], ct);

        await using (var seed = await conn.BeginTransactionAsync(ct))
        {
            await SetContextAsync(conn, seed, orgId: null, platform: true, ct);
            await ExecAsync(conn, seed,
                "INSERT INTO platform_audit_events (id, occurred_at, actor, action, org_id, detail_json) " +
                "VALUES (@id, now(), 'test', 'entitlement.grant', @org, '{}')", ct,
                ("id", UuidV7.NewId()), ("org", orgA));
            await seed.CommitAsync(ct);
        }

        const string Update = "UPDATE platform_audit_events SET actor = 'tamper'";
        const string Delete = "DELETE FROM platform_audit_events";

        var tenantUpdate = await ShouldFailAsync(conn, orgId: orgA, platform: false, Update, ct);
        tenantUpdate.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        tenantUpdate.MessageText.ShouldContain("permission denied");

        var tenantDelete = await ShouldFailAsync(conn, orgId: orgA, platform: false, Delete, ct);
        tenantDelete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        tenantDelete.MessageText.ShouldContain("permission denied");

        var platformUpdate = await ShouldFailAsync(conn, orgId: null, platform: true, Update, ct);
        platformUpdate.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        platformUpdate.MessageText.ShouldContain("permission denied");

        var platformDelete = await ShouldFailAsync(conn, orgId: null, platform: true, Delete, ct);
        platformDelete.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        platformDelete.MessageText.ShouldContain("permission denied");
    }

    /// <summary>
    /// <c>feature_flags</c> is the one platform table a tenant session may READ. Reads are ungated on
    /// purpose: the capability resolver reads a flag inside the ambient request transaction so a
    /// money-path kill switch takes effect immediately instead of waiting out a cache TTL, and there
    /// is no safe way to have platform scope in there — <c>PlatformScopedExecutor</c> opens its own
    /// transaction, and a <c>SET LOCAL</c> would persist to end of transaction and leave the rest of
    /// the request with platform scope, defeating org isolation on the other two tables.
    /// <para>
    /// So the property under test is <b>toggling</b>, not reading. The grant is intentionally retained
    /// (the CLI runs as the app role), which means the tenant-plane UPDATE/DELETE do not throw — they
    /// are filtered to zero rows by the write policy. INSERT is the one that raises, because only the
    /// write policy's WITH CHECK applies to it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Feature_flags_are_tenant_readable_but_only_platform_writable()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();
        var flag = $"probe-{Guid.NewGuid():N}";

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA], ct);

        await using (var seed = await conn.BeginTransactionAsync(ct))
        {
            await SetContextAsync(conn, seed, orgId: null, platform: true, ct);
            await ExecAsync(conn, seed,
                "INSERT INTO feature_flags (name, enabled, updated_at, updated_by) " +
                "VALUES (@name, false, now(), 'platform')", ct, ("name", flag));
            await seed.CommitAsync(ct);
        }

        try
        {
            // Readable inside a tenant session — this is what lets a kill switch be honored in-transaction.
            var underTenant = await CountAsync(
                conn, "SELECT count(*) FROM feature_flags WHERE name = @name",
                orgId: orgA, platform: false, ct, ("name", flag));
            underTenant.ShouldBe(1, "the resolver must be able to read a flag in the request transaction");

            // And with no context at all, since a flag has no org to key on. This is also what lets
            // CapabilityRegistryValidator read the table at startup without opening platform scope.
            var withoutContext = await CountAsync(
                conn, "SELECT count(*) FROM feature_flags WHERE name = @name",
                orgId: null, platform: false, ct, ("name", flag));
            withoutContext.ShouldBe(1);

            // Writes: INSERT raises, UPDATE/DELETE are filtered to zero rows.
            var insert = await ShouldFailAsync(conn, orgId: orgA, platform: false,
                "INSERT INTO feature_flags (name, enabled, updated_at, updated_by) " +
                "VALUES ('tenant-planted', true, now(), 'tenant')", ct);
            insert.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            insert.MessageText.ShouldContain("new row violates row-level security policy");

            await using (var tamper = await conn.BeginTransactionAsync(ct))
            {
                await SetContextAsync(conn, tamper, orgId: orgA, platform: false, ct);

                var toggled = await ExecAsync(conn, tamper,
                    "UPDATE feature_flags SET enabled = true WHERE name = @name", ct, ("name", flag));
                toggled.ShouldBe(0, "RLS, not the grant, is what stops a tenant-plane flag toggle");

                var dropped = await ExecAsync(conn, tamper,
                    "DELETE FROM feature_flags WHERE name = @name", ct, ("name", flag));
                dropped.ShouldBe(0, "nor may a tenant delete a flag to fall back to its default");

                await tamper.CommitAsync(ct);
            }

            (await ReadFlagAsync(conn, flag, ct)).ShouldBe(false, "the flag is still off after both attempts");
        }
        finally
        {
            await DeleteFlagAsPlatformAsync(conn, flag, ct);
        }
    }

    /// <summary>
    /// The positive control for the test above: <c>feature_flags</c> deliberately has no
    /// <c>RevokeAppendOnly</c>, because a flag is mutable state (unlike an entitlement, which is an
    /// append-only grant event). Without this, adding the revoke would leave every "affects zero rows"
    /// assertion passing for the wrong reason and break the CLI's toggle for good.
    /// </summary>
    [Fact]
    public async Task Platform_scope_can_toggle_a_feature_flag()
    {
        var ct = TestContext.Current.CancellationToken;
        var flag = $"probe-{Guid.NewGuid():N}";

        await using var conn = await fixture.OpenAppConnectionAsync(ct);

        await using (var seed = await conn.BeginTransactionAsync(ct))
        {
            await SetContextAsync(conn, seed, orgId: null, platform: true, ct);
            await ExecAsync(conn, seed,
                "INSERT INTO feature_flags (name, enabled, updated_at, updated_by) " +
                "VALUES (@name, false, now(), 'platform')", ct, ("name", flag));
            await seed.CommitAsync(ct);
        }

        try
        {
            await using (var toggle = await conn.BeginTransactionAsync(ct))
            {
                await SetContextAsync(conn, toggle, orgId: null, platform: true, ct);
                var updated = await ExecAsync(conn, toggle,
                    "UPDATE feature_flags SET enabled = true, updated_by = 'ops' WHERE name = @name", ct,
                    ("name", flag));
                updated.ShouldBe(1, "a flag is mutable state — the platform plane must be able to flip it");
                await toggle.CommitAsync(ct);
            }

            (await ReadFlagAsync(conn, flag, ct)).ShouldBe(true);
        }
        finally
        {
            await DeleteFlagAsPlatformAsync(conn, flag, ct);
        }
    }

    // ── The GUC itself ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pooling-safety property. <c>SET LOCAL</c> reverts the placeholder to <c>''</c> (empty
    /// string, not NULL) at commit, and the policies compare it to the literal text <c>'on'</c> with
    /// no boolean cast, so <c>''</c> simply does not match. A session-level SET here would hand the
    /// next tenant to pick up this connection a live platform scope.
    /// </summary>
    [Fact]
    public async Task Platform_scope_does_not_leak_past_its_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await SeedOrgsAsync(conn, [orgA], ct);
        await GrantEntitlementAsPlatformAsync(conn, orgA, Capability, ct);

        // Same physical connection, after a committed platform-scoped unit of work.
        await using var after = new NpgsqlCommand("SELECT current_setting('app.platform', true)", conn);
        var leaked = await after.ExecuteScalarAsync(ct) as string;
        string.IsNullOrEmpty(leaked).ShouldBeTrue($"app.platform leaked as '{leaked}' after commit");

        var visible = await CountAsync(conn, "SELECT count(*) FROM entitlements", orgId: null, platform: false, ct);
        visible.ShouldBe(0, "and the escape it granted is gone with it");
    }

    // ── PlatformScopedExecutor ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlatformScopedExecutor_opens_the_escape_and_closes_it_on_commit()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();
        var id = UuidV7.NewId();

        await using (var seedConn = await fixture.OpenAppConnectionAsync(ct))
        {
            await SeedOrgsAsync(seedConn, [orgA], ct);
        }

        await using var db = fixture.CreateContext(fixture.AppConnectionString);

        // Hold the connection open ourselves so EF cannot return it to the pool (and reset the GUC)
        // between the executor's commit and the leak check below.
        await db.Database.OpenConnectionAsync(ct);
        var executor = new PlatformScopedExecutor(db);

        await executor.RunAsync(async () =>
        {
            // Succeeding at all is the assertion: this INSERT is rejected with 42501 outside platform
            // scope, so it can only pass if RunAsync set the GUC on this transaction.
            await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO entitlements (id, org_id, capability, granted, effective_at, actor)
                 VALUES ({id}, {orgA}, {Capability}, true, now(), 'platform-executor')
                 """, ct);
        }, ct);

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await using (var leakCheck = new NpgsqlCommand("SELECT current_setting('app.platform', true)", conn))
        {
            var leaked = await leakCheck.ExecuteScalarAsync(ct) as string;
            string.IsNullOrEmpty(leaked).ShouldBeTrue($"app.platform leaked as '{leaked}' after RunAsync");
        }

        // Committed, not merely attempted.
        var persisted = await CountAsync(
            conn, "SELECT count(*) FROM entitlements WHERE id = @id",
            orgId: null, platform: true, ct, ("id", id));
        persisted.ShouldBe(1);
    }

    [Fact]
    public async Task PlatformScopedExecutor_rolls_back_and_rethrows_when_the_work_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgA = UuidV7.NewId();
        var id = UuidV7.NewId();

        await using (var seedConn = await fixture.OpenAppConnectionAsync(ct))
        {
            await SeedOrgsAsync(seedConn, [orgA], ct);
        }

        await using var db = fixture.CreateContext(fixture.AppConnectionString);
        var executor = new PlatformScopedExecutor(db);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await executor.RunAsync(async () =>
            {
                await db.Database.ExecuteSqlAsync(
                    $"""
                     INSERT INTO entitlements (id, org_id, capability, granted, effective_at, actor)
                     VALUES ({id}, {orgA}, {Capability}, true, now(), 'platform-executor')
                     """, ct);
                throw new InvalidOperationException("work failed");
            }, ct));

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        var persisted = await CountAsync(
            conn, "SELECT count(*) FROM entitlements WHERE id = @id",
            orgId: null, platform: true, ct, ("id", id));
        persisted.ShouldBe(0, "a failed platform-scoped unit of work must leave nothing behind");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private const string EntitlementInsertSql =
        "INSERT INTO entitlements (id, org_id, capability, granted, effective_at, actor) " +
        "VALUES (@id, @org, @cap, true, now(), @actor)";

    private const string CohortInsertSql =
        "INSERT INTO capability_cohorts (id, capability, org_id, added_at, added_by) " +
        "VALUES (@id, @cap, @org, now(), @by)";

    /// <summary>
    /// Sets the transaction-local context under test. <c>orgId</c> null leaves <c>app.org_id</c>
    /// untouched (the fail-closed case); <c>platform</c> false leaves <c>app.platform</c> untouched.
    /// Both are <c>set_config(..., is_local =&gt; true)</c> — the parameterized <c>SET LOCAL</c>.
    /// </summary>
    private static async Task SetContextAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid? orgId, bool platform, CancellationToken ct)
    {
        if (orgId is { } org)
        {
            await RlsProbe.SetOrgAsync(conn, tx, org, ct);
        }

        if (platform)
        {
            await RlsProbe.SetPlatformAsync(conn, tx, ct);
        }
    }

    /// <summary>
    /// Runs one statement under the given context and asserts it raised. Each call gets its OWN
    /// transaction on purpose: a rejected statement puts Postgres into a failed transaction, so a
    /// second statement in the same one would report 25P02 (in_failed_sql_transaction) and the test
    /// would be asserting the wrong thing entirely.
    /// </summary>
    private static async Task<PostgresException> ShouldFailAsync(
        NpgsqlConnection conn, Guid? orgId, bool platform, string sql, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await SetContextAsync(conn, tx, orgId, platform, ct);

        return await Should.ThrowAsync<PostgresException>(async () => await ExecAsync(conn, tx, sql, ct));
    }

    /// <summary>
    /// Restores the shared, global <c>feature_flags</c> table. Mandatory, not tidiness: the probe names
    /// these tests plant are not in the capability registry, and
    /// <c>CapabilityRegistryValidator</c> fails host startup on exactly that. A leaked row therefore
    /// stops every sibling test that boots an <c>ApiFactory</c> — the table has no <c>org_id</c>, so
    /// minting a fresh org buys no isolation here.
    /// </summary>
    private static async Task DeleteFlagAsPlatformAsync(
        NpgsqlConnection conn, string flag, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await SetContextAsync(conn, tx, orgId: null, platform: true, ct);
        await ExecAsync(conn, tx, "DELETE FROM feature_flags WHERE name = @name", ct, ("name", flag));
        await tx.CommitAsync(ct);
    }

    /// <summary>Reads a flag's current value under platform scope — the plane that can always see it.</summary>
    private static async Task<bool> ReadFlagAsync(NpgsqlConnection conn, string flag, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await SetContextAsync(conn, tx, orgId: null, platform: true, ct);

        await using var cmd = new NpgsqlCommand("SELECT enabled FROM feature_flags WHERE name = @name", conn, tx);
        cmd.Parameters.AddWithValue("name", flag);
        var value = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return value;
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection conn, string sql, Guid? orgId, bool platform, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await SetContextAsync(conn, tx, orgId, platform, ct);

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return count;
    }

    private static async Task SeedOrgsAsync(NpgsqlConnection conn, Guid[] orgIds, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var id in orgIds)
        {
            await ExecAsync(conn, tx,
                "INSERT INTO orgs (id, name, created_at) VALUES (@id, 'test', now())", ct, ("id", id));
        }

        await tx.CommitAsync(ct);
    }

    private static async Task SeedAuditEventAsync(NpgsqlConnection conn, Guid orgId, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await SetContextAsync(conn, tx, orgId, platform: false, ct);
        await RlsProbe.InsertEventAsync(conn, tx, orgId, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task GrantEntitlementAsPlatformAsync(
        NpgsqlConnection conn, Guid orgId, string capability, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await SetContextAsync(conn, tx, orgId: null, platform: true, ct);
        await ExecAsync(conn, tx, EntitlementInsertSql, ct,
            ("id", UuidV7.NewId()), ("org", orgId), ("cap", capability), ("actor", "platform"));
        await tx.CommitAsync(ct);
    }

    private static async Task<Guid> AddCohortAsPlatformAsync(
        NpgsqlConnection conn, Guid orgId, string capability, string addedBy, CancellationToken ct)
    {
        var id = UuidV7.NewId();
        await using var tx = await conn.BeginTransactionAsync(ct);
        await SetContextAsync(conn, tx, orgId: null, platform: true, ct);
        await ExecAsync(conn, tx, CohortInsertSql, ct,
            ("id", id), ("cap", capability), ("org", orgId), ("by", addedBy));
        await tx.CommitAsync(ct);
        return id;
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
