using System.Text.Json;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Adapters;
using LeaseBook.Web.Capabilities;
using Npgsql;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The <c>capabilities</c> verb's write surface (ADR-028, Task 12), driven through the real host
/// services so it exercises <c>PlatformScopedExecutor</c> — the single <c>app.platform</c> escape —
/// exactly as the CLI process does.
/// <para>
/// The load-bearing property here is <b>atomicity of the audit trail</b>: every mutation writes one
/// <c>platform_audit_events</c> row in the same transaction as its state change, and a rejected write
/// leaves neither. <c>platform_audit_events</c> is append-only in BOTH planes, so these rows cannot be
/// cleaned up afterwards — assertions are therefore scoped by a per-test <c>since</c> timestamp and,
/// where possible, a freshly minted org id, rather than by counting the table.
/// </para>
/// <para>
/// <c>feature_flags</c> IS global and shared, so every test that writes one deletes it in a
/// <c>finally</c> and issues a <c>pg_notify</c>, matching the sibling helpers: a leaked row for an
/// unregistered name fails startup validation for every sibling host, and a leaked row for a
/// registered one carries flipped state into an unrelated test for up to a cache TTL.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CapabilitiesCommandTests(PostgresFixture fixture)
{
    private const string Capability = "consolidated-statements";

    // ── Flags ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Flag_enable_writes_the_row_and_exactly_one_audit_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var since = await NowAsync(ct);

        try
        {
            var (exit, output, _) = await RunAsync(["capabilities", "flag", "enable", Capability]);

            exit.ShouldBe(0);
            output.ShouldContain("ENABLED");
            (await ReadFlagAsync(Capability, ct)).ShouldBe(true);

            var audits = await ReadAuditsAsync("flag.enable", Capability, since, ct);
            audits.Count.ShouldBe(1, "one verb writes exactly one platform audit row");
            audits[0].Actor.ShouldBe(CapabilitiesCommand.Actor);
            audits[0].OrgId.ShouldBeNull("a flag is deployment-wide, so the audit row names no org");

            var detail = JsonDocument.Parse(audits[0].Detail).RootElement;
            detail.GetProperty("enabled").GetBoolean().ShouldBeTrue();
            detail.GetProperty("previous").ValueKind.ShouldBe(
                JsonValueKind.Null, "there was no row before, which is NOT the same as an explicit kill");
        }
        finally
        {
            await DeleteFlagAsync(Capability, ct);
        }
    }

    /// <summary>
    /// A flag is mutable state, so the write destroys the previous value: if the audit row does not
    /// carry it, it is gone for good. An entitlement needs no equivalent — its history IS the table.
    /// </summary>
    [Fact]
    public async Task Flag_disable_records_the_value_it_overwrote()
    {
        var ct = TestContext.Current.CancellationToken;
        var since = await NowAsync(ct);

        try
        {
            (await RunAsync(["capabilities", "flag", "enable", Capability])).Exit.ShouldBe(0);
            var (exit, output, _) = await RunAsync(["capabilities", "flag", "disable", Capability]);

            exit.ShouldBe(0);
            output.ShouldContain("KILLED");
            (await ReadFlagAsync(Capability, ct)).ShouldBe(false);

            var audits = await ReadAuditsAsync("flag.disable", Capability, since, ct);
            audits.Count.ShouldBe(1);

            var detail = JsonDocument.Parse(audits[0].Detail).RootElement;
            detail.GetProperty("enabled").GetBoolean().ShouldBeFalse();
            detail.GetProperty("previous").GetBoolean().ShouldBeTrue();
        }
        finally
        {
            await DeleteFlagAsync(Capability, ct);
        }
    }

    /// <summary>
    /// W4: the <c>NOTIFY</c> must be issued INSIDE the write transaction. Postgres queues
    /// notifications and delivers them after commit, so by the time this listener is woken the row it
    /// was told about is already visible — which is what the second assertion proves. Issued after the
    /// commit instead, that ordering guarantee is gone and the race returns.
    /// </summary>
    [Fact]
    public async Task A_flag_toggle_notifies_only_once_the_row_is_visible()
    {
        var ct = TestContext.Current.CancellationToken;
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var listener = await fixture.OpenAppConnectionAsync(ct);
        listener.Notification += (_, args) => received.TrySetResult(args.Payload);

        await using (var listen = new NpgsqlCommand(
            $"LISTEN {CapabilityNotificationListener.Channel}", listener))
        {
            await listen.ExecuteNonQueryAsync(ct);
        }

        try
        {
            (await RunAsync(["capabilities", "flag", "enable", Capability])).Exit.ShouldBe(0);

            // Wait to completion BEFORE touching the connection again — Npgsql forbids issuing a
            // command while a wait is in flight.
            var delivered = await listener.WaitAsync(TimeSpan.FromSeconds(20), ct);
            delivered.ShouldBeTrue("the writer must issue NOTIFY, and inside its own transaction");
            (await received.Task).ShouldBe(Capability, "the payload names the capability that moved");

            // Read on the LISTENER's own connection, which holds no transaction: the row must
            // already be committed and visible, because the notification was delivered after commit.
            await using var read = new NpgsqlCommand(
                "SELECT enabled FROM feature_flags WHERE name = @name", listener);
            read.Parameters.AddWithValue("name", Capability);
            (await read.ExecuteScalarAsync(ct)).ShouldBe(
                true, "delivery after commit means the change is visible when the wake-up arrives");
        }
        finally
        {
            await DeleteFlagAsync(Capability, ct);
        }
    }

    // ── Entitlements ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A revoke is a new row with <c>granted = false</c>, never an update — the table has no
    /// UPDATE/DELETE grant in either plane. Both events survive, and current state is the latest.
    /// </summary>
    [Fact]
    public async Task Grant_then_revoke_appends_two_events_and_two_audit_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = UuidV7.NewId();
        await SeedOrgAsync(org, ct);

        (await RunAsync(["capabilities", "grant", Capability, "--org", org.ToString()])).Exit.ShouldBe(0);
        (await RunAsync(["capabilities", "revoke", Capability, "--org", org.ToString()])).Exit.ShouldBe(0);

        var events = await ReadEntitlementsAsync(org, ct);
        events.Count.ShouldBe(2, "entitlements are append-only events, so the grant is still there");
        events[^1].Granted.ShouldBeFalse("the latest row per (org, capability) is the current state");
        events[0].Actor.ShouldBe(CapabilitiesCommand.Actor);

        var audits = await ReadAuditsForOrgAsync(org, ct);
        audits.Select(a => a.Action).ShouldBe(["entitlement.grant", "entitlement.revoke"]);
        audits.ShouldAllBe(a => a.Capability == Capability);
    }

    /// <summary>
    /// <b>The atomicity proof that discriminates.</b> Every row Postgres stores carries <c>xmin</c>,
    /// the id of the transaction that inserted it, so two rows written in one transaction have equal
    /// <c>xmin</c> and rows written in two transactions cannot. That is a direct observation of the
    /// property, not a proxy for it.
    /// <para>
    /// The rollback test below is necessary but NOT sufficient on its own: the FK fires on the
    /// entitlement INSERT, which precedes the audit write under "one transaction" and under
    /// "state first, audit separately" alike, so its emptiness carries no discriminating
    /// information. The failure mode that separates the two designs — state row present, audit row
    /// absent — is what <c>xmin</c> equality rules out. Verified by splitting the audit write into a
    /// second <c>PlatformScopedExecutor.RunAsync</c> in a scratch edit and watching this go red.
    /// </para>
    /// <para>
    /// The timestamp equality is the weaker, secondary signal: both rows carry the single
    /// <c>SELECT now()</c> the transaction opened with, so it also documents that decision.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_state_row_and_its_audit_row_are_written_by_one_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = UuidV7.NewId();
        await SeedOrgAsync(org, ct);

        (await RunAsync(["capabilities", "grant", Capability, "--org", org.ToString()])).Exit.ShouldBe(0);

        var (stateXmin, stateAt) = await ReadRowIdentityAsync(
            "SELECT xmin::text, effective_at FROM entitlements WHERE org_id = @org", org, ct);
        var (auditXmin, auditAt) = await ReadRowIdentityAsync(
            "SELECT xmin::text, occurred_at FROM platform_audit_events WHERE org_id = @org", org, ct);

        auditXmin.ShouldBe(
            stateXmin,
            "the entitlement and its audit row must be inserted by the SAME Postgres transaction — " +
            "equal xmin is the only evidence that rules out 'state committed, audit written after'");

        auditAt.ShouldBe(
            stateAt,
            "both carry the single SELECT now() the transaction opened with (transaction start time)");
    }

    /// <summary>
    /// The rollback half. The FK to <c>orgs</c> rejects the entitlement, so the whole platform-scoped
    /// transaction rolls back and the audit row, added in the same <c>SaveChanges</c>, goes with it.
    /// Read
    /// <see cref="The_state_row_and_its_audit_row_are_written_by_one_transaction"/> for why this test
    /// alone would not establish atomicity.
    /// </summary>
    [Fact]
    public async Task A_rejected_write_leaves_neither_a_state_row_nor_an_audit_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var ghost = UuidV7.NewId(); // never inserted into orgs

        var (exit, _, errors) = await RunAsync(["capabilities", "grant", Capability, "--org", ghost.ToString()]);

        exit.ShouldBe(1);
        errors.ShouldContain("does not exist");

        (await ReadEntitlementsAsync(ghost, ct)).ShouldBeEmpty();
        (await ReadAuditsForOrgAsync(ghost, ct)).ShouldBeEmpty(
            "the audit row is written in the same transaction, so a rolled-back write leaves none");
    }

    // ── Cohorts ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cohort_add_writes_the_rule_and_one_audit_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = UuidV7.NewId();
        var user = UuidV7.NewId();
        await SeedOrgAsync(org, ct);

        var (exit, _, _) = await RunAsync(
            ["capabilities", "cohort", "add", Capability, "--org", org.ToString(), "--user", user.ToString()]);

        exit.ShouldBe(0);

        var rows = await ReadCohortsAsync(org, ct);
        rows.Count.ShouldBe(1);
        rows[0].UserId.ShouldBe(user);
        rows[0].AddedBy.ShouldBe(CapabilitiesCommand.Actor);

        var audits = await ReadAuditsForOrgAsync(org, ct);
        audits.Count.ShouldBe(1);
        audits[0].Action.ShouldBe("cohort.add");
        JsonDocument.Parse(audits[0].Detail).RootElement
            .GetProperty("user_id").GetString().ShouldBe(user.ToString());
    }

    /// <summary>
    /// <c>remove</c> is the exact inverse of <c>add</c>, which is what stops the CLI creating state it
    /// cannot undo. <c>capability_cohorts</c> keeps its UPDATE/DELETE grants on purpose (membership is
    /// mutable, unlike an entitlement), so this is an ordinary delete rather than a compensating event.
    /// </summary>
    [Fact]
    public async Task Cohort_remove_deletes_the_rule_and_records_the_removal()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = UuidV7.NewId();
        await SeedOrgAsync(org, ct);

        (await RunAsync(["capabilities", "cohort", "add", Capability, "--org", org.ToString()]))
            .Exit.ShouldBe(0);

        var (exit, output, _) = await RunAsync(
            ["capabilities", "cohort", "remove", Capability, "--org", org.ToString()]);

        exit.ShouldBe(0);
        output.ShouldContain("removed");
        (await ReadCohortsAsync(org, ct)).ShouldBeEmpty();

        var audits = await ReadAuditsForOrgAsync(org, ct);
        audits.Select(a => a.Action).ShouldBe(["cohort.add", "cohort.remove"]);
        JsonDocument.Parse(audits[^1].Detail).RootElement.GetProperty("removed").GetInt32().ShouldBe(1);
    }

    /// <summary>
    /// Without <c>--user</c>, <c>remove</c> targets the org-wide rule ONLY. Anything looser would let a
    /// bare <c>--org</c> silently destroy user-level rules the operator never named.
    /// </summary>
    [Fact]
    public async Task Cohort_remove_without_a_user_leaves_user_level_rules_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = UuidV7.NewId();
        var user = UuidV7.NewId();
        await SeedOrgAsync(org, ct);

        (await RunAsync(["capabilities", "cohort", "add", Capability, "--org", org.ToString()]))
            .Exit.ShouldBe(0);
        (await RunAsync(
            ["capabilities", "cohort", "add", Capability, "--org", org.ToString(), "--user", user.ToString()]))
            .Exit.ShouldBe(0);

        (await RunAsync(["capabilities", "cohort", "remove", Capability, "--org", org.ToString()]))
            .Exit.ShouldBe(0);

        var remaining = await ReadCohortsAsync(org, ct);
        remaining.Count.ShouldBe(1);
        remaining[0].UserId.ShouldBe(user, "only the org-wide rule was named, so only it was removed");
    }

    /// <summary>
    /// A removal matching nothing is refused rather than reported as a successful no-op: the likely
    /// cause is a mistyped org, and "removed 0 rules" is how an operator concludes a cohort is gone
    /// when it is not. Refused inside the transaction, so it writes no audit row either.
    /// </summary>
    [Fact]
    public async Task Cohort_remove_matching_nothing_is_refused_and_records_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = UuidV7.NewId();
        await SeedOrgAsync(org, ct);

        var (exit, _, errors) = await RunAsync(
            ["capabilities", "cohort", "remove", Capability, "--org", org.ToString()]);

        exit.ShouldBe(1);
        errors.ShouldContain("no '" + Capability + "' cohort rule exists");
        (await ReadAuditsForOrgAsync(org, ct)).ShouldBeEmpty();
    }

    // ── Refusals write nothing at all ───────────────────────────────────────────────────────────

    /// <summary>
    /// W1 end to end. <c>money-path-fixture</c> is IN the registry, so a naive registry check passes
    /// it; the CLI must still refuse. See <c>CapabilitiesVerb.FixtureRefusal</c> for the blast radius.
    /// </summary>
    [Fact]
    public async Task The_money_path_fixture_is_refused_and_nothing_is_written()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureName = CapabilityCatalog.MoneyPathFixture.Name;
        var since = await NowAsync(ct);

        var (exit, _, errors) = await RunAsync(["capabilities", "flag", "enable", fixtureName]);

        exit.ShouldBe(1);
        errors.ShouldContain(fixtureName);
        errors.ShouldContain("409");

        (await ReadFlagAsync(fixtureName, ct)).ShouldBeNull("no feature_flags row may be created");
        (await ReadAuditsAsync("flag.enable", fixtureName, since, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_capability_is_refused_and_nothing_is_written()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Typo = "consolidated-statments";

        var (exit, _, errors) = await RunAsync(["capabilities", "flag", "enable", Typo]);

        exit.ShouldBe(1);
        errors.ShouldContain("unknown capability");

        // The whole point: no row is created, so CapabilityRegistryValidator never sees this name.
        (await ReadFlagAsync(Typo, ct)).ShouldBeNull();
    }

    // ── List ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_prints_every_registry_capability()
    {
        var (exit, output, _) = await RunAsync(["capabilities", "list"]);

        exit.ShouldBe(0);
        foreach (var capability in CapabilityCatalog.All)
        {
            output.ShouldContain(capability.Name);
        }

        output.ShouldContain("ENTITLED");
        output.ShouldContain("COHORTS");
    }

    /// <summary>
    /// The listing must be able to answer the question the entitlement-collision message sends an
    /// operator to ask: did the earlier grant land? A flags-only listing could not, which is why this
    /// asserts on the entitlement and cohort state of a specific org rather than on the header row.
    /// <para>
    /// It also pins that the listing runs under platform scope: <c>entitlements</c> and
    /// <c>capability_cohorts</c> are cross-org readable only under <c>app.platform</c>, and without it
    /// this would print "(no entitlement event)" for a row that exists, with no error anywhere.
    /// </para>
    /// </summary>
    [Fact]
    public async Task List_for_an_org_reports_entitlement_and_cohort_state()
    {
        var org = UuidV7.NewId();
        await SeedOrgAsync(org, TestContext.Current.CancellationToken);

        (await RunAsync(["capabilities", "grant", Capability, "--org", org.ToString()])).Exit.ShouldBe(0);
        (await RunAsync(["capabilities", "cohort", "add", Capability, "--org", org.ToString()]))
            .Exit.ShouldBe(0);

        var (exit, output, _) = await RunAsync(["capabilities", "list", "--org", org.ToString()]);

        exit.ShouldBe(0);
        output.ShouldContain(org.ToString());
        output.ShouldContain("granted");
        output.ShouldContain("org-wide");
        // The fixture has no entitlement for this org: absence is stated, never left blank.
        output.ShouldContain("(no entitlement event)");
    }

    // ── The actor convention ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Without <see cref="CapabilitiesCommand.OperatorVariable"/> the actor is process identity, which
    /// in the ACA job degrades to <c>cli:root@&lt;ephemeral-container-id&gt;</c> — honest, but it
    /// attributes a change to nobody. With it, the accountable party is named AND the process is still
    /// recorded, because "who decided" and "where did it run" are different questions. These rows are
    /// append-only, so the convention has to be right from the first write.
    /// </summary>
    [Fact]
    public void The_actor_names_the_operator_when_one_is_configured()
    {
        var process = CapabilitiesCommand.BuildActor(null);
        process.ShouldStartWith("cli:");

        var attributed = CapabilitiesCommand.BuildActor("  ops-jane  ");
        attributed.ShouldStartWith("operator:ops-jane");
        attributed.Contains(process, StringComparison.Ordinal)
            .ShouldBeTrue("the process identity is kept alongside the operator, never replaced by it");

        CapabilitiesCommand.BuildActor("   ").ShouldBe(process, "a blank value is not an attribution");
    }

    /// <summary>
    /// <c>--stale</c> parses today so Task 13 adds behavior rather than grammar. Until then it says so
    /// out loud: an operator must never read an empty staleness report as "nothing is stale".
    /// </summary>
    [Fact]
    public async Task List_stale_parses_and_says_the_age_report_is_not_available_yet()
    {
        var (exit, output, _) = await RunAsync(["capabilities", "list", "--stale"]);

        exit.ShouldBe(0);
        output.ShouldContain("--stale");
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the verb against the real host services, capturing the console the operator would see.
    /// Redirection is safe because every test in <c>DatabaseCollection</c> runs sequentially.
    /// </summary>
    private async Task<(int Exit, string Output, string Errors)> RunAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        var errors = new StringWriter();

        try
        {
            Console.SetOut(output);
            Console.SetError(errors);
            var exit = await CapabilitiesCommand.RunAsync(fixture.Api.Services, args);
            return (exit, output.ToString(), errors.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private async Task<DateTime> NowAsync(CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT now()", conn);
        return (DateTime)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task SeedOrgAsync(Guid orgId, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO orgs (id, name, created_at) VALUES (@id, 'capabilities-cli-test', now())", conn);
        cmd.Parameters.AddWithValue("id", orgId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Null when no row exists — which resolves as the registry default, not as a kill.</summary>
    private async Task<bool?> ReadFlagAsync(string name, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT enabled FROM feature_flags WHERE name = @name", conn);
        cmd.Parameters.AddWithValue("name", name);
        return await cmd.ExecuteScalarAsync(ct) as bool?;
    }

    /// <summary>
    /// Restores the shared, global flag state. The delete DOES notify, matching the sibling helpers:
    /// any host still running in this collection drops its cached set immediately rather than carrying
    /// flipped state for up to a TTL into an unrelated test.
    /// </summary>
    private async Task DeleteFlagAsync(string name, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await PlatformScopeAsync(conn, tx, ct);

        await using (var cmd = new NpgsqlCommand("DELETE FROM feature_flags WHERE name = @name", conn, tx))
        {
            cmd.Parameters.AddWithValue("name", name);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var signal = new NpgsqlCommand(
            $"SELECT pg_notify('{CapabilityNotificationListener.Channel}', @name)", conn, tx))
        {
            signal.Parameters.AddWithValue("name", name);
            await signal.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private async Task<List<(string Action, string? Capability, Guid? OrgId, string Actor, string Detail)>>
        ReadAuditsAsync(string action, string capability, DateTime since, CancellationToken ct) =>
        await ReadAuditsAsync(
            "SELECT action, capability, org_id, actor, detail_json FROM platform_audit_events " +
            "WHERE action = @action AND capability = @capability AND occurred_at >= @since " +
            "ORDER BY occurred_at, id",
            ct,
            ("action", action), ("capability", capability), ("since", since));

    private async Task<List<(string Action, string? Capability, Guid? OrgId, string Actor, string Detail)>>
        ReadAuditsForOrgAsync(Guid orgId, CancellationToken ct) =>
        await ReadAuditsAsync(
            "SELECT action, capability, org_id, actor, detail_json FROM platform_audit_events " +
            "WHERE org_id = @org ORDER BY occurred_at, id",
            ct,
            ("org", orgId));

    /// <summary>
    /// Platform scope, because <c>platform_audit_events_platform_only</c> hides these rows from every
    /// tenant session whatever its org context.
    /// </summary>
    private async Task<List<(string Action, string? Capability, Guid? OrgId, string Actor, string Detail)>>
        ReadAuditsAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        var rows = new List<(string, string?, Guid?, string, string)>();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await PlatformScopeAsync(conn, tx, ct);

        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// Reads a row's inserting transaction id and its timestamp. <c>xmin</c> is a system column every
    /// heap row carries, so two rows written by one transaction share it and rows written by two
    /// cannot — which is what makes the atomicity assertion an observation rather than a proxy.
    /// </summary>
    private async Task<(string Xmin, DateTime At)> ReadRowIdentityAsync(
        string sql, Guid orgId, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await PlatformScopeAsync(conn, tx, ct);

        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("org", orgId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        (await reader.ReadAsync(ct)).ShouldBeTrue($"expected exactly one row from: {sql}");
        var row = (reader.GetString(0), reader.GetDateTime(1));
        (await reader.ReadAsync(ct)).ShouldBeFalse($"expected exactly one row from: {sql}");

        return row;
    }

    private async Task<List<(bool Granted, string Actor)>> ReadEntitlementsAsync(
        Guid orgId, CancellationToken ct)
    {
        var rows = new List<(bool, string)>();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await PlatformScopeAsync(conn, tx, ct);

        await using (var cmd = new NpgsqlCommand(
            "SELECT granted, actor FROM entitlements WHERE org_id = @org ORDER BY effective_at, granted DESC",
            conn, tx))
        {
            cmd.Parameters.AddWithValue("org", orgId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetBoolean(0), reader.GetString(1)));
            }
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    private async Task<List<(Guid? UserId, string AddedBy)>> ReadCohortsAsync(Guid orgId, CancellationToken ct)
    {
        var rows = new List<(Guid?, string)>();

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await PlatformScopeAsync(conn, tx, ct);

        await using (var cmd = new NpgsqlCommand(
            "SELECT user_id, added_by FROM capability_cohorts WHERE org_id = @org", conn, tx))
        {
            cmd.Parameters.AddWithValue("org", orgId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetString(1)));
            }
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    private static async Task PlatformScopeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.platform', 'on', true)", conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
