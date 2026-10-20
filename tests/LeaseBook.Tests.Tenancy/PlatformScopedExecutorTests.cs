using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Tenancy.Support;
using LeaseBook.Web.Tenancy;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Tenancy;

/// <summary>
/// <see cref="PlatformScopedExecutor"/>, the only production setter of the platform escape
/// (ADR-028). It holds the escape for its own transaction and no longer: Npgsql pools connections,
/// so a setting that outlived the transaction would open the escape for whatever request picked
/// the connection up next.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PlatformScopedExecutorTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_escape_is_open_inside_its_transaction_and_closed_after_it_on_the_same_session()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var executor = new PlatformScopedExecutor(session.Db);
        (await session.ReadAsync(ct)).Platform.ShouldBeNull();

        var inside = await executor.RunAsync(async () =>
        {
            session.Db.Database.CurrentTransaction.ShouldNotBeNull();
            return await session.ReadAsync(ct);
        }, ct);
        var after = await session.ReadAsync(ct);

        inside.Platform.ShouldBe("on");
        inside.Org.ShouldBeNull("the platform plane states no organization");
        inside.Persona.ShouldBeNull();
        after.BackendPid.ShouldBe(inside.BackendPid);
        after.Platform.ShouldBeNullOrEmpty();
        session.Transactions.Events.ShouldBe(["begin", "commit"]);
    }

    [Fact]
    public async Task The_void_form_runs_the_work_under_the_escape()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        SessionContext? seen = null;

        await new PlatformScopedExecutor(session.Db).RunAsync(async () => seen = await session.ReadAsync(ct), ct);

        seen.ShouldNotBeNull();
        seen.Platform.ShouldBe("on");
        (await session.ReadAsync(ct)).Platform.ShouldBeNullOrEmpty();
    }

    [Fact]
    public async Task Work_that_throws_is_rolled_back_and_leaves_the_escape_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var failure = new InvalidOperationException("platform work failed");

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            new PlatformScopedExecutor(session.Db).RunAsync(() => throw failure, ct));

        thrown.ShouldBeSameAs(failure);
        session.Transactions.Events.ShouldBe(["begin", "rollback"]);
        (await session.ReadAsync(ct)).Platform.ShouldBeNullOrEmpty();
    }

    /// <summary>
    /// What the escape is for: <c>feature_flags</c> is readable by anyone and writable only on the
    /// platform plane. A write inside the unit of work lands; the same write on the same session once
    /// it has ended is refused by row-level security.
    /// </summary>
    [Fact]
    public async Task A_platform_only_write_lands_inside_the_unit_of_work_and_is_refused_after_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var inside = $"tenancy-test-{UuidV7.NewId():N}";
        var after = $"tenancy-test-{UuidV7.NewId():N}";

        await new PlatformScopedExecutor(session.Db).RunAsync(() => InsertFlagAsync(session, inside, ct), ct);
        var refused = await Should.ThrowAsync<PostgresException>(() => InsertFlagAsync(session, after, ct));

        refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await FlagCountAsync(session, inside, ct)).ShouldBe(1);
        (await FlagCountAsync(session, after, ct)).ShouldBe(0);
    }

    [Fact]
    public async Task It_refuses_to_nest_inside_a_transaction_already_open_on_the_context()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        await using var outer = await session.Db.Database.BeginTransactionAsync(ct);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            new PlatformScopedExecutor(session.Db).RunAsync(() => Task.CompletedTask, ct));

        ex.Message.ShouldStartWith("A scoped unit of work cannot nest");
        (await session.ReadAsync(ct)).Platform.ShouldBeNull();
    }

    /// <summary>
    /// Both forms name the missing work, ahead of everything the shared bracket checks: with no
    /// context either, it is still the work that is reported.
    /// </summary>
    [Fact]
    public async Task Missing_work_is_refused_by_both_forms_before_anything_else()
    {
        var ct = TestContext.Current.CancellationToken;

        (await Should.ThrowAsync<ArgumentNullException>(() =>
                new PlatformScopedExecutor(null!).RunAsync((Func<Task>)null!, ct)))
            .ParamName.ShouldBe("work");
        (await Should.ThrowAsync<ArgumentNullException>(() =>
                new PlatformScopedExecutor(null!).RunAsync((Func<Task<int>>)null!, ct)))
            .ParamName.ShouldBe("work");

        await using var session = await Session.OpenAsync(fixture, ct);
        (await Should.ThrowAsync<ArgumentNullException>(() =>
                new PlatformScopedExecutor(session.Db).RunAsync((Func<Task>)null!, ct)))
            .ParamName.ShouldBe("work");
        session.Transactions.Events.ShouldBeEmpty();
    }

    private static async Task InsertFlagAsync(Session session, string name, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO feature_flags (name, enabled, updated_at, updated_by) VALUES (@name, false, now(), 'tenancy-test')",
            session.Connection,
            TenancyDb.TransactionOf(session.Db));
        cmd.Parameters.AddWithValue("name", name);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> FlagCountAsync(Session session, string name, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM feature_flags WHERE name = @name", session.Connection);
        cmd.Parameters.AddWithValue("name", name);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
