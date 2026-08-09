using System.Text.Json;
using LeaseBook.Modules.Capabilities.Contracts;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Tests.Integration.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The capability write seam (ADR-028), asserted through <see cref="ICapabilityAdmin"/> — the
/// interface every caller crosses — rather than through the CLI verb that happens to be its only
/// caller today. What the verb prints is a separate concern, covered in <c>CapabilitiesCommandTests</c>.
/// <para>
/// The load-bearing property here is <b>atomicity of the audit trail</b>: every mutation writes one
/// <c>platform_audit_events</c> row in the same transaction as its state change, and a refused write
/// leaves neither. <c>platform_audit_events</c> is append-only in BOTH planes, so these rows cannot be
/// cleaned up afterwards — assertions are therefore scoped by a per-test <c>since</c> timestamp and,
/// where possible, a freshly minted org id, rather than by counting the table.
/// </para>
/// <para>
/// <c>feature_flags</c> IS global and shared, so every test that writes one deletes it in a
/// <c>finally</c>: a leaked row for an unregistered name fails startup validation for every sibling
/// host, and a leaked row for a registered one carries flipped state into an unrelated test for up to
/// a cache TTL.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CapabilityAdminTests(PostgresFixture fixture)
{
    private const string Capability = "consolidated-statements";
    private const string Actor = "admin-test";

    private CapabilityStateProbe Probe => new(fixture);

    // ── Flags ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Enable_writes_the_row_and_exactly_one_audit_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var since = await Probe.NowAsync(ct);

        try
        {
            await AdminAsync(a => a.EnableFlagAsync(Capability, Actor, ct));

            (await Probe.ReadFlagAsync(Capability, ct)).ShouldBe(true);

            var audits = await Probe.ReadAuditsAsync("flag.enable", Capability, since, ct);
            audits.Count.ShouldBe(1, "one mutation writes exactly one platform audit row");
            audits[0].Actor.ShouldBe(Actor);
            audits[0].OrgId.ShouldBeNull("a flag is deployment-wide, so the audit row names no org");

            var detail = JsonDocument.Parse(audits[0].Detail).RootElement;
            detail.GetProperty("enabled").GetBoolean().ShouldBeTrue();
            detail.GetProperty("previous").ValueKind.ShouldBe(
                JsonValueKind.Null, "there was no row before, which is NOT the same as an explicit kill");
        }
        finally
        {
            await Probe.DeleteFlagAsync(Capability, ct);
        }
    }

    /// <summary>
    /// A flag is mutable state, so the write destroys the previous value: if the audit row does not
    /// carry it, it is gone for good. An entitlement needs no equivalent — its history IS the table.
    /// </summary>
    [Fact]
    public async Task Disable_records_the_value_it_overwrote()
    {
        var ct = TestContext.Current.CancellationToken;
        var since = await Probe.NowAsync(ct);

        try
        {
            await AdminAsync(a => a.EnableFlagAsync(Capability, Actor, ct));
            await AdminAsync(a => a.DisableFlagAsync(Capability, Actor, ct));

            (await Probe.ReadFlagAsync(Capability, ct)).ShouldBe(false);

            var audits = await Probe.ReadAuditsAsync("flag.disable", Capability, since, ct);
            audits.Count.ShouldBe(1);

            var detail = JsonDocument.Parse(audits[0].Detail).RootElement;
            detail.GetProperty("enabled").GetBoolean().ShouldBeFalse();
            detail.GetProperty("previous").GetBoolean().ShouldBeTrue();
        }
        finally
        {
            await Probe.DeleteFlagAsync(Capability, ct);
        }
    }

    [Fact]
    public async Task Clear_deletes_the_override_and_audits_the_previous_value()
    {
        var ct = TestContext.Current.CancellationToken;

        try
        {
            await AdminAsync(a => a.DisableFlagAsync(Capability, Actor, ct));
            var since = await Probe.NowAsync(ct);

            await AdminAsync(a => a.ClearFlagAsync(Capability, Actor, ct));

            (await Probe.ReadFlagAsync(Capability, ct)).ShouldBeNull(
                "absence restores cohort/registry resolution; explicit false does not");

            var audits = await Probe.ReadAuditsAsync("flag.clear", Capability, since, ct);
            audits.Count.ShouldBe(1);
            JsonDocument.Parse(audits[0].Detail).RootElement
                .GetProperty("previous").GetBoolean().ShouldBeFalse();
        }
        finally
        {
            await Probe.DeleteFlagAsync(Capability, ct);
        }
    }

    [Fact]
    public async Task Clear_matching_nothing_is_refused_and_records_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await Probe.DeleteFlagAsync(Capability, ct);
        var since = await Probe.NowAsync(ct);

        var refusal = await Should.ThrowAsync<CapabilityRefusedException>(
            async () => await AdminAsync(a => a.ClearFlagAsync(Capability, Actor, ct)));

        refusal.Message.ShouldContain("no explicit flag override");
        (await Probe.ReadAuditsAsync("flag.clear", Capability, since, ct)).ShouldBeEmpty();
    }

    /// <summary>
    /// The <c>NOTIFY</c> must be issued INSIDE the write transaction. Postgres queues notifications and
    /// delivers them after commit, so by the time this listener is woken the row it was told about is
    /// already visible — which is what the second assertion proves. Issued after the commit instead,
    /// that ordering guarantee is gone and the race returns.
    /// </summary>
    [Fact]
    public async Task A_toggle_notifies_only_once_the_row_is_visible()
    {
        var ct = TestContext.Current.CancellationToken;
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var listener = await fixture.OpenAppConnectionAsync(ct);
        listener.Notification += (_, args) => received.TrySetResult(args.Payload);

        await using (var listen = new NpgsqlCommand(
            $"LISTEN {LeaseBook.Modules.Capabilities.Caching.CapabilityNotifications.Channel}", listener))
        {
            await listen.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await AdminAsync(a => a.EnableFlagAsync(Capability, Actor, ct));

            (await listener.WaitAsync(TimeSpan.FromSeconds(20), ct)).ShouldBeTrue();
            (await received.Task).ShouldBe(Capability);
            (await Probe.ReadFlagAsync(Capability, ct)).ShouldBe(
                true, "the notification arrives only after the transaction that wrote the row committed");
        }
        finally
        {
            await Probe.DeleteFlagAsync(Capability, ct);
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
        await Probe.SeedOrgAsync(org, ct);

        await AdminAsync(a => a.GrantAsync(Capability, org, Actor, ct));
        await AdminAsync(a => a.RevokeAsync(Capability, org, Actor, ct));

        var events = await Probe.ReadEntitlementsAsync(org, ct);
        events.Count.ShouldBe(2, "entitlements are append-only events, so the grant is still there");
        events[^1].Granted.ShouldBeFalse("the latest row per (org, capability) is the current state");
        events[0].Actor.ShouldBe(Actor);

        var audits = await Probe.ReadAuditsForOrgAsync(org, ct);
        audits.Select(a => a.Action).ShouldBe(["entitlement.grant", "entitlement.revoke"]);
        audits.ShouldAllBe(a => a.Capability == Capability);
    }

    /// <summary>
    /// <b>The atomicity proof that discriminates.</b> Every row Postgres stores carries <c>xmin</c>,
    /// the id of the (sub)transaction that inserted it. Rows written by two different transactions can
    /// never share it, which is the property being relied on here.
    /// <para>
    /// <b>What this asserts is slightly stronger than "same transaction", deliberately.</b> <c>xmin</c>
    /// holds the SUBtransaction xid for rows inserted inside a savepoint, and EF opens a savepoint per
    /// <c>SaveChanges</c> when a transaction is already open — so a refactor that kept one transaction
    /// but split the write across two <c>SaveChanges</c> calls would also turn this red. That is the
    /// right direction to err: stricter than advertised, and it can never be a false green.
    /// </para>
    /// <para>
    /// The rollback test below is necessary but NOT sufficient on its own: the FK fires on the
    /// entitlement INSERT, which precedes the audit write under "one transaction" and under "state
    /// first, audit separately" alike, so its emptiness carries no discriminating information. The
    /// failure mode that separates the two designs — state row present, audit row absent — is what
    /// <c>xmin</c> equality rules out.
    /// </para>
    /// <para>
    /// The timestamp equality proves something different and weaker, and is kept for that: both rows
    /// carry the single <c>SELECT now()</c> the transaction opened with. It is NOT atomicity evidence.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_state_row_and_its_audit_row_are_written_by_one_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = UuidV7.NewId();
        await Probe.SeedOrgAsync(org, ct);

        await AdminAsync(a => a.GrantAsync(Capability, org, Actor, ct));

        var (stateXmin, stateAt) = await Probe.ReadRowIdentityAsync(
            "SELECT xmin::text, effective_at FROM entitlements WHERE org_id = @org", org, ct);
        var (auditXmin, auditAt) = await Probe.ReadRowIdentityAsync(
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
    /// </summary>
    [Fact]
    public async Task A_rejected_write_leaves_neither_a_state_row_nor_an_audit_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var ghost = UuidV7.NewId(); // never inserted into orgs

        await Should.ThrowAsync<DbUpdateException>(
            async () => await AdminAsync(a => a.GrantAsync(Capability, ghost, Actor, ct)));

        (await Probe.ReadEntitlementsAsync(ghost, ct)).ShouldBeEmpty();
        (await Probe.ReadAuditsForOrgAsync(ghost, ct)).ShouldBeEmpty(
            "the audit row is written in the same transaction, so a rolled-back write leaves none");
    }

    // ── Cohorts ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cohort_add_writes_the_rule_and_one_audit_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = UuidV7.NewId();
        var user = UuidV7.NewId();
        await Probe.SeedOrgAsync(org, ct);
        await Probe.SeedUserAsync(user, org, ct);

        await AdminAsync(a => a.AddToCohortAsync(Capability, org, user, Actor, ct));

        var rows = await Probe.ReadCohortsAsync(org, ct);
        rows.Count.ShouldBe(1);
        rows[0].UserId.ShouldBe(user);
        rows[0].AddedBy.ShouldBe(Actor);

        var audits = await Probe.ReadAuditsForOrgAsync(org, ct);
        audits.Count.ShouldBe(1);
        audits[0].Action.ShouldBe("cohort.add");
        JsonDocument.Parse(audits[0].Detail).RootElement
            .GetProperty("user_id").GetString().ShouldBe(user.ToString());
    }

    /// <summary>
    /// <c>remove</c> is the exact inverse of <c>add</c>, which is what stops a caller creating state it
    /// cannot undo. <c>capability_cohorts</c> keeps its UPDATE/DELETE grants on purpose (membership is
    /// mutable, unlike an entitlement), so this is an ordinary delete rather than a compensating event.
    /// </summary>
    [Fact]
    public async Task Cohort_remove_deletes_the_rule_and_records_the_removal()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = UuidV7.NewId();
        await Probe.SeedOrgAsync(org, ct);

        await AdminAsync(a => a.AddToCohortAsync(Capability, org, null, Actor, ct));
        await AdminAsync(a => a.RemoveFromCohortAsync(Capability, org, null, Actor, ct));

        (await Probe.ReadCohortsAsync(org, ct)).ShouldBeEmpty();

        var audits = await Probe.ReadAuditsForOrgAsync(org, ct);
        audits.Select(a => a.Action).ShouldBe(["cohort.add", "cohort.remove"]);
        JsonDocument.Parse(audits[^1].Detail).RootElement.GetProperty("removed").GetInt32().ShouldBe(1);
    }

    /// <summary>
    /// Without a named user, <c>remove</c> targets the org-wide rule ONLY. Anything looser would let a
    /// bare org silently destroy user-level rules the caller never named.
    /// </summary>
    [Fact]
    public async Task Cohort_remove_without_a_user_leaves_user_level_rules_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = UuidV7.NewId();
        var user = UuidV7.NewId();
        await Probe.SeedOrgAsync(org, ct);
        await Probe.SeedUserAsync(user, org, ct);

        await AdminAsync(a => a.AddToCohortAsync(Capability, org, null, Actor, ct));
        await AdminAsync(a => a.AddToCohortAsync(Capability, org, user, Actor, ct));

        await AdminAsync(a => a.RemoveFromCohortAsync(Capability, org, null, Actor, ct));

        var remaining = await Probe.ReadCohortsAsync(org, ct);
        remaining.Count.ShouldBe(1);
        remaining[0].UserId.ShouldBe(user, "only the org-wide rule was named, so only it was removed");
    }

    [Fact]
    public async Task Cohort_add_refuses_a_user_that_does_not_exist_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = UuidV7.NewId();
        var missingUser = UuidV7.NewId();
        await Probe.SeedOrgAsync(org, ct);

        var refusal = await Should.ThrowAsync<CapabilityRefusedException>(
            async () => await AdminAsync(a => a.AddToCohortAsync(Capability, org, missingUser, Actor, ct)));

        refusal.Message.ShouldContain("does not exist in org");
        (await Probe.ReadCohortsAsync(org, ct)).ShouldBeEmpty();
        (await Probe.ReadAuditsForOrgAsync(org, ct)).ShouldBeEmpty();
    }

    /// <summary>
    /// The tenancy half of the same check: <c>asp_net_users</c> is RLS-exempt, so nothing in the
    /// database stops a cohort rule naming a user who belongs to a different organization.
    /// </summary>
    [Fact]
    public async Task Cohort_add_refuses_a_user_from_another_org_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var requestedOrg = UuidV7.NewId();
        var usersOrg = UuidV7.NewId();
        var user = UuidV7.NewId();
        await Probe.SeedOrgAsync(requestedOrg, ct);
        await Probe.SeedOrgAsync(usersOrg, ct);
        await Probe.SeedUserAsync(user, usersOrg, ct);

        var refusal = await Should.ThrowAsync<CapabilityRefusedException>(
            async () => await AdminAsync(a => a.AddToCohortAsync(Capability, requestedOrg, user, Actor, ct)));

        refusal.Message.ShouldContain("does not exist in org");
        (await Probe.ReadCohortsAsync(requestedOrg, ct)).ShouldBeEmpty();
        (await Probe.ReadAuditsForOrgAsync(requestedOrg, ct)).ShouldBeEmpty();
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
        await Probe.SeedOrgAsync(org, ct);

        var refusal = await Should.ThrowAsync<CapabilityRefusedException>(
            async () => await AdminAsync(a => a.RemoveFromCohortAsync(Capability, org, null, Actor, ct)));

        refusal.Message.ShouldContain($"no '{Capability}' cohort rule exists");
        (await Probe.ReadAuditsForOrgAsync(org, ct)).ShouldBeEmpty();
    }

    // ── The registry rule holds at the seam, not only at the CLI ────────────────────────────────

    /// <summary>
    /// The CLI refuses both of these earlier and with a better message. These assert the same rule at
    /// the interface, where it holds for callers that never touch argv — which is the difference
    /// between a check one entry point performs and an invariant.
    /// </summary>
    [Fact]
    public async Task A_fixture_capability_is_refused_and_nothing_is_written()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixtureName = CapabilityCatalog.MoneyPathFixture.Name;
        var since = await Probe.NowAsync(ct);

        var refusal = await Should.ThrowAsync<CapabilityRefusedException>(
            async () => await AdminAsync(a => a.EnableFlagAsync(fixtureName, Actor, ct)));

        refusal.Message.ShouldContain("fixture");
        (await Probe.ReadFlagAsync(fixtureName, ct)).ShouldBeNull("no feature_flags row may be created");
        (await Probe.ReadAuditsAsync("flag.enable", fixtureName, since, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unregistered_capability_is_refused_and_nothing_is_written()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Typo = "consolidated-statments";

        var refusal = await Should.ThrowAsync<CapabilityRefusedException>(
            async () => await AdminAsync(a => a.EnableFlagAsync(Typo, Actor, ct)));

        refusal.Message.ShouldContain("registry");

        // The whole point: no row is created, so CapabilityRegistryValidator never sees this name.
        (await Probe.ReadFlagAsync(Typo, ct)).ShouldBeNull();
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the seam from the real host container and calls it. A scope of its own, because the
    /// member opens its own platform-scoped transaction and a scoped unit of work refuses to nest.
    /// </summary>
    private async Task<DateTime> AdminAsync(Func<ICapabilityAdmin, Task<DateTime>> work)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<ICapabilityAdmin>());
    }
}
