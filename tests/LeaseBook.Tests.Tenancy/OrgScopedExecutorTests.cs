using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Tenancy.Support;
using Shouldly;

namespace LeaseBook.Tests.Tenancy;

/// <summary>
/// <see cref="OrgScopedExecutor"/>, the only production setter of the organization-plane context
/// (#314, ADR-048): one transaction, the organization, persona and user set inside it in one
/// statement, the work, then commit or roll back. Each test reads back what the session saw, as the
/// app role, on a connection of its own (<see cref="Session"/>).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OrgScopedExecutorTests(PostgresFixture fixture)
{
    private static readonly Guid UserId = Guid.Parse("0199f0aa-1111-7222-8333-944455556666");

    private static Actor ActorFor(bool system) => system ? Actor.System("test-harness") : Actor.User(UserId);

    [Theory]
    [InlineData(false, Persona.None, "none")]
    [InlineData(false, Persona.Staff, "staff")]
    [InlineData(false, Persona.Tenant, "tenant")]
    [InlineData(false, Persona.Owner, "owner")]
    [InlineData(true, Persona.None, "none")]
    [InlineData(true, Persona.System, "system")]
    [InlineData(true, Persona.Staff, "staff")]
    public async Task The_unit_of_work_sets_the_org_persona_and_user_inside_its_transaction(
        bool system, Persona persona, string personaText)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var orgId = UuidV7.NewId();
        var actor = ActorFor(system);

        var seen = await session.Executor.RunAsync(orgId, actor, persona, async () =>
        {
            session.Db.Database.CurrentTransaction.ShouldNotBeNull();
            session.Org.OrgId.ShouldBe(orgId);
            session.Actor.Actor.ShouldBe(actor);
            return await session.ReadAsync(ct);
        }, ct);

        seen.Org.ShouldBe(orgId.ToString());
        seen.Persona.ShouldBe(personaText);
        // A system actor's user is written as '' — which every grant reads as NULL — never left unset.
        seen.User.ShouldBe(system ? "" : UserId.ToString());
        seen.Platform.ShouldBeNull("the organization plane never opens the platform escape");
    }

    [Fact]
    public async Task A_system_actor_writes_an_empty_user_on_a_session_that_never_had_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        (await session.ReadAsync(ct)).User.ShouldBeNull("a fresh session has never had a user set");

        var seen = await session.Executor.RunAsSystemAsync(UuidV7.NewId(), "test-harness",
            () => session.ReadAsync(ct), ct);

        seen.User.ShouldBe("");
    }

    [Fact]
    public async Task Nothing_it_sets_survives_the_transaction_on_the_same_session()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var before = await session.ReadAsync(ct);
        before.Org.ShouldBeNull();
        before.Persona.ShouldBeNull();
        before.User.ShouldBeNull();

        var inside = await session.Executor.RunAsync(UuidV7.NewId(), Actor.User(UserId), Persona.Staff,
            () => session.ReadAsync(ct), ct);
        var after = await session.ReadAsync(ct);

        after.BackendPid.ShouldBe(inside.BackendPid, "the same server session before and after");
        after.Org.ShouldBeNullOrEmpty();
        after.Persona.ShouldBeNullOrEmpty();
        after.User.ShouldBeNullOrEmpty();
    }

    [Fact]
    public async Task Nothing_it_sets_survives_a_unit_of_work_that_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var failure = new InvalidOperationException("the work failed");

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            session.Executor.RunAsync(UuidV7.NewId(), Actor.User(UserId), Persona.Owner,
                () => throw failure, ct));

        thrown.ShouldBeSameAs(failure);
        var after = await session.ReadAsync(ct);
        after.Org.ShouldBeNullOrEmpty();
        after.Persona.ShouldBeNullOrEmpty();
        after.User.ShouldBeNullOrEmpty();
        session.Db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Fact]
    public async Task Work_that_succeeds_is_committed()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await TenancyDb.NewOrgAsync(fixture, ct);
        await using var session = await Session.OpenAsync(fixture, ct);

        await session.Executor.RunAsSystemAsync(orgId, "test-harness", () => session.InsertEventAsync(orgId, ct), ct);

        (await TenancyDb.CommittedEventsAsync(fixture, orgId, ct)).ShouldBe(1);
        session.Transactions.Events.ShouldBe(["begin", "commit"]);
    }

    [Fact]
    public async Task Work_that_throws_is_rolled_back_and_its_exception_reaches_the_caller()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await TenancyDb.NewOrgAsync(fixture, ct);
        await using var session = await Session.OpenAsync(fixture, ct);
        var failure = new InvalidOperationException("after the write");

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            session.Executor.RunAsSystemAsync(orgId, "test-harness", async () =>
            {
                await session.InsertEventAsync(orgId, ct);
                throw failure;
            }, ct));

        thrown.ShouldBeSameAs(failure);
        (await TenancyDb.CommittedEventsAsync(fixture, orgId, ct)).ShouldBe(0);
        session.Transactions.Events.ShouldBe(["begin", "rollback"]);
    }

    [Fact]
    public async Task The_value_returning_form_returns_what_the_work_returned()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);

        var answer = await session.Executor.RunAsync(UuidV7.NewId(), Actor.User(UserId), Persona.Staff,
            () => Task.FromResult(42), ct);

        answer.ShouldBe(42);
    }

    [Fact]
    public async Task The_void_form_runs_the_work_in_the_unit_of_work()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var orgId = UuidV7.NewId();
        SessionContext? seen = null;

        await session.Executor.RunAsync(orgId, Actor.User(UserId), Persona.Tenant,
            async () => seen = await session.ReadAsync(ct), ct);

        seen.ShouldNotBeNull();
        seen.Org.ShouldBe(orgId.ToString());
        seen.Persona.ShouldBe("tenant");
        seen.User.ShouldBe(UserId.ToString());
        session.Transactions.Events.ShouldBe(["begin", "commit"]);
    }

    [Fact]
    public async Task System_work_runs_as_the_named_process_under_the_system_persona()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var orgId = UuidV7.NewId();
        SessionContext? seenVoid = null;
        Actor? actorVoid = null;

        await session.Executor.RunAsSystemAsync(orgId, "invariant-sweep", async () =>
        {
            actorVoid = session.Actor.Actor;
            seenVoid = await session.ReadAsync(ct);
        }, ct);
        var (actorValue, seenValue) = await session.Executor.RunAsSystemAsync(orgId, "seed:demo",
            async () => (session.Actor.Actor, await session.ReadAsync(ct)), ct);

        actorVoid!.Reference.ShouldBe("system:invariant-sweep");
        seenVoid!.Persona.ShouldBe("system");
        seenVoid.Org.ShouldBe(orgId.ToString());
        actorValue!.Reference.ShouldBe("system:seed:demo");
        seenValue.Persona.ShouldBe("system");
        seenValue.User.ShouldBe("");
    }

    [Fact]
    public async Task System_work_refuses_a_process_name_that_is_not_an_identifier()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);

        await Should.ThrowAsync<ArgumentException>(() =>
            session.Executor.RunAsSystemAsync(UuidV7.NewId(), "Nightly sweep", () => Task.CompletedTask, ct));
        await Should.ThrowAsync<ArgumentException>(() =>
            session.Executor.RunAsSystemAsync(UuidV7.NewId(), "", () => Task.FromResult(1), ct));
        session.Transactions.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_contexts_show_the_unit_of_work_inside_it_and_what_they_held_before_it_after()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var outerOrg = UuidV7.NewId();
        var outerActor = Actor.System("outer-process");
        session.Org.OrgId = outerOrg;
        session.Actor.Actor = outerActor;
        var orgId = UuidV7.NewId();
        var actor = Actor.User(UserId);

        await session.Executor.RunAsync(orgId, actor, Persona.Staff, () =>
        {
            session.Org.OrgId.ShouldBe(orgId);
            session.Actor.Actor.ShouldBe(actor);
            ((IActorContext)session.Actor).UserId.ShouldBe(UserId);
            return Task.CompletedTask;
        }, ct);

        session.Org.OrgId.ShouldBe(outerOrg);
        session.Actor.Actor.ShouldBe(outerActor);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            session.Executor.RunAsync(orgId, actor, Persona.Staff, () => throw new InvalidOperationException(), ct));

        session.Org.OrgId.ShouldBe(outerOrg);
        session.Actor.Actor.ShouldBe(outerActor);
    }

    [Fact]
    public async Task The_contexts_are_empty_again_after_a_unit_of_work_that_began_with_none()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);

        await session.Executor.RunAsync(UuidV7.NewId(), Actor.User(UserId), Persona.Staff, () => Task.CompletedTask, ct);

        session.Org.OrgId.ShouldBeNull();
        session.Actor.Actor.ShouldBeNull();
        ((IActorContext)session.Actor).UserId.ShouldBeNull();
    }

    [Fact]
    public async Task An_empty_org_id_is_refused_before_the_database_is_touched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var ran = false;

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            session.Executor.RunAsync(Guid.Empty, Actor.User(UserId), Persona.Staff, () =>
            {
                ran = true;
                return Task.CompletedTask;
            }, ct));

        ex.ParamName.ShouldBe("orgId");
        ex.Message.ShouldBe(
            "OrgScopedExecutor requires a non-empty org id — running org-scoped work with no context would let " +
            "RLS silently return empty results. (Parameter 'orgId')");
        ran.ShouldBeFalse();
        session.Transactions.Events.ShouldBeEmpty();
        session.Org.OrgId.ShouldBeNull();
    }

    [Fact]
    public async Task An_empty_org_id_is_reported_ahead_of_an_open_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        await using var outer = await session.Db.Database.BeginTransactionAsync(ct);

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            session.Executor.RunAsSystemAsync(Guid.Empty, "test-harness", () => Task.CompletedTask, ct));

        ex.ParamName.ShouldBe("orgId");
    }

    [Fact]
    public async Task A_missing_actor_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);

        var ex = await Should.ThrowAsync<ArgumentNullException>(() =>
            session.Executor.RunAsync(UuidV7.NewId(), null!, Persona.Staff, () => Task.FromResult(1), ct));

        ex.ParamName.ShouldBe("actor");
        session.Transactions.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_work_is_refused_by_both_forms_before_anything_else()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);

        (await Should.ThrowAsync<ArgumentNullException>(() =>
                session.Executor.RunAsync(UuidV7.NewId(), Actor.User(UserId), Persona.Staff, (Func<Task>)null!, ct)))
            .ParamName.ShouldBe("work");
        // An empty org id too, so the value form's own check is the one that answers.
        (await Should.ThrowAsync<ArgumentNullException>(() =>
                session.Executor.RunAsync(Guid.Empty, Actor.User(UserId), Persona.Staff, (Func<Task<int>>)null!, ct)))
            .ParamName.ShouldBe("work");
        session.Transactions.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_user_cannot_run_under_the_system_persona()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var ran = false;

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            session.Executor.RunAsync(UuidV7.NewId(), Actor.User(UserId), Persona.System, () =>
            {
                ran = true;
                return Task.CompletedTask;
            }, ct));

        ex.ParamName.ShouldBe("persona");
        ex.Message.ShouldBe(
            $"A user actor (user:{UserId}) cannot run under the system persona. Resolve the persona from the " +
            "user's roles; only a named system process is organization-wide by fiat. (Parameter 'persona')");
        ran.ShouldBeFalse();
        session.Transactions.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Persona.Tenant, "tenant")]
    [InlineData(Persona.Owner, "owner")]
    public async Task A_system_actor_cannot_run_under_a_portal_persona(Persona persona, string personaText)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var ran = false;

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            session.Executor.RunAsync(UuidV7.NewId(), Actor.System("test-harness"), persona, () =>
            {
                ran = true;
                return Task.CompletedTask;
            }, ct));

        ex.ParamName.ShouldBe("persona");
        ex.Message.ShouldBe(
            $"The {personaText} persona needs a user: its rows are granted through that user's portal link, and " +
            "a system actor (system:test-harness) has none. (Parameter 'persona')");
        ran.ShouldBeFalse();
        session.Transactions.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_persona_that_is_not_defined_is_refused_before_the_database_is_touched(bool system)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);

        var ex = await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            session.Executor.RunAsync(UuidV7.NewId(), ActorFor(system), (Persona)42, () => Task.CompletedTask, ct));

        ex.ParamName.ShouldBe("persona");
        session.Transactions.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_unit_of_work_cannot_open_another_on_the_same_context()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var orgId = UuidV7.NewId();

        var inner = await session.Executor.RunAsync(orgId, Actor.User(UserId), Persona.Staff, async () =>
            await Should.ThrowAsync<InvalidOperationException>(() =>
                session.Executor.RunAsSystemAsync(UuidV7.NewId(), "test-harness", () => Task.CompletedTask, ct)), ct);

        inner.Message.ShouldStartWith("A scoped unit of work cannot nest");
    }

    [Fact]
    public async Task Units_of_work_on_two_contexts_each_see_only_their_own_org()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var outer = await Session.OpenAsync(fixture, ct);
        await using var inner = await Session.OpenAsync(fixture, ct);
        var outerOrg = UuidV7.NewId();
        var innerOrg = UuidV7.NewId();

        var (outerSeen, innerSeen) = await outer.Executor.RunAsync(outerOrg, Actor.User(UserId), Persona.Staff,
            async () =>
            {
                var nested = await inner.Executor.RunAsSystemAsync(innerOrg, "test-harness", () => inner.ReadAsync(ct), ct);
                return (await outer.ReadAsync(ct), nested);
            }, ct);

        outerSeen.Org.ShouldBe(outerOrg.ToString());
        outerSeen.Persona.ShouldBe("staff");
        innerSeen.Org.ShouldBe(innerOrg.ToString());
        innerSeen.Persona.ShouldBe("system");
    }

    /// <summary>
    /// What the settings are for. Two organizations hold rows; under row-level security the app role
    /// reads only the unit of work's own organization's, and nothing at all under a persona the gate
    /// does not admit there.
    /// </summary>
    [Theory]
    [InlineData(false, Persona.Staff, 2)]
    [InlineData(true, Persona.System, 2)]
    [InlineData(true, Persona.Staff, 2)]
    [InlineData(false, Persona.None, 0)]
    [InlineData(true, Persona.None, 0)]
    [InlineData(false, Persona.Tenant, 0)] // audit_events grants a portal persona inserts, never reads
    [InlineData(false, Persona.Owner, 0)]
    public async Task Row_level_security_admits_the_org_and_persona_the_unit_of_work_set(
        bool system, Persona persona, long expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var mine = await TenancyDb.NewOrgAsync(fixture, ct);
        var theirs = await TenancyDb.NewOrgAsync(fixture, ct);
        await TenancyDb.PlantEventsAsync(fixture, mine, 2, ct);
        await TenancyDb.PlantEventsAsync(fixture, theirs, 3, ct);
        await using var session = await Session.OpenAsync(fixture, ct);

        var seen = await session.Executor.RunAsync(mine, ActorFor(system), persona,
            () => session.CountEventsAsync(ct), ct);

        seen.ShouldBe(expected);
        (await session.CountEventsAsync(ct)).ShouldBe(0, "outside the unit of work the session has no organization");
    }
}
