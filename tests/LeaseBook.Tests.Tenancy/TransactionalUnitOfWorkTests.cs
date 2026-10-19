using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Tenancy.Support;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LeaseBook.Tests.Tenancy;

/// <summary>
/// <see cref="TransactionalUnitOfWork"/>, the bracket both scoping planes share: one transaction,
/// the caller's context set inside it first, the work, then commit, or an explicit rollback when
/// anything in it throws. It knows no setting of its own, so these tests set the organization
/// through the probe in <c>configure</c>, as an executor would set its own.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class TransactionalUnitOfWorkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Configure_runs_inside_the_transaction_before_the_work_and_the_work_is_committed()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await TenancyDb.NewOrgAsync(fixture, ct);
        await using var session = await Session.OpenAsync(fixture, ct);
        var log = session.Transactions;

        var result = await TransactionalUnitOfWork.RunAsync(
            session.Db,
            async token =>
            {
                session.Db.Database.CurrentTransaction.ShouldNotBeNull();
                log.Note("configure");
                await RlsProbe.SetOrgAsync(session.Db, orgId, token);
            },
            async () =>
            {
                log.Note("work");
                await session.InsertEventAsync(orgId, ct);
                return "done";
            },
            ct);

        result.ShouldBe("done");
        log.Events.ShouldBe(["begin", "configure", "work", "commit"]);
        session.Db.Database.CurrentTransaction.ShouldBeNull();
        (await TenancyDb.CommittedEventsAsync(fixture, orgId, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task The_void_form_runs_the_work_and_commits_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await TenancyDb.NewOrgAsync(fixture, ct);
        await using var session = await Session.OpenAsync(fixture, ct);

        await TransactionalUnitOfWork.RunAsync(
            session.Db,
            token => RlsProbe.SetOrgAsync(session.Db, orgId, token),
            () => session.InsertEventAsync(orgId, ct),
            ct);

        session.Transactions.Events.ShouldBe(["begin", "commit"]);
        (await TenancyDb.CommittedEventsAsync(fixture, orgId, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Work_that_throws_is_rolled_back_explicitly_and_its_exception_reaches_the_caller()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = await TenancyDb.NewOrgAsync(fixture, ct);
        await using var session = await Session.OpenAsync(fixture, ct);
        var failure = new InvalidOperationException("after the write");

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            TransactionalUnitOfWork.RunAsync<int>(
                session.Db,
                token => RlsProbe.SetOrgAsync(session.Db, orgId, token),
                async () =>
                {
                    await session.InsertEventAsync(orgId, ct);
                    throw failure;
                },
                ct));

        thrown.ShouldBeSameAs(failure);
        // An explicit rollback, not a transaction merely disposed: only the former reaches the
        // interceptor, as it reaches every other observer of the context's transactions.
        session.Transactions.Events.ShouldBe(["begin", "rollback"]);
        session.Db.Database.CurrentTransaction.ShouldBeNull();
        (await TenancyDb.CommittedEventsAsync(fixture, orgId, ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Configure_that_throws_is_rolled_back_and_the_work_never_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        var failure = new InvalidOperationException("configure failed");
        var ran = false;

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            TransactionalUnitOfWork.RunAsync(
                session.Db,
                _ => throw failure,
                () =>
                {
                    ran = true;
                    return Task.CompletedTask;
                },
                ct));

        thrown.ShouldBeSameAs(failure);
        ran.ShouldBeFalse();
        session.Transactions.Events.ShouldBe(["begin", "rollback"]);
    }

    [Fact]
    public async Task It_refuses_to_nest_inside_a_transaction_already_open_on_the_context()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        await using var outer = await session.Db.Database.BeginTransactionAsync(ct);
        var called = false;

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            TransactionalUnitOfWork.RunAsync(
                session.Db,
                _ =>
                {
                    called = true;
                    return Task.CompletedTask;
                },
                () =>
                {
                    called = true;
                    return Task.CompletedTask;
                },
                ct));

        ex.Message.ShouldBe(
            "A scoped unit of work cannot nest: this DbContext already has a transaction open. Context is set " +
            "with SET LOCAL and dies with the transaction that set it, so an inner unit of work would be " +
            "operating under the outer transaction's context, not its own. Out-of-band work (a job, the CLI, " +
            "a cache refresh) must resolve its own scope so it gets its own DbContext; in-request work must " +
            "read on the ambient transaction instead of opening a second one.");
        called.ShouldBeFalse();
        session.Db.Database.CurrentTransaction.ShouldBeSameAs(outer, "the outer transaction is left as it was");
        session.Transactions.Events.ShouldBe(["begin"]);
    }

    [Fact]
    public async Task Two_contexts_may_each_hold_a_unit_of_work_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var outer = await Session.OpenAsync(fixture, ct);
        await using var inner = await Session.OpenAsync(fixture, ct);

        var result = await TransactionalUnitOfWork.RunAsync(
            outer.Db,
            _ => Task.CompletedTask,
            () => TransactionalUnitOfWork.RunAsync(inner.Db, _ => Task.CompletedTask, () => Task.FromResult(7), ct),
            ct);

        result.ShouldBe(7);
        outer.Transactions.Events.ShouldBe(["begin", "commit"]);
        inner.Transactions.Events.ShouldBe(["begin", "commit"]);
    }

    [Fact]
    public async Task Missing_arguments_are_refused_by_name_before_a_transaction_opens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await Session.OpenAsync(fixture, ct);
        Func<CancellationToken, Task> configure = _ => Task.CompletedTask;

        (await Should.ThrowAsync<ArgumentNullException>(() =>
                TransactionalUnitOfWork.RunAsync(null!, configure, () => Task.FromResult(1), ct)))
            .ParamName.ShouldBe("db");
        (await Should.ThrowAsync<ArgumentNullException>(() =>
                TransactionalUnitOfWork.RunAsync(session.Db, null!, () => Task.FromResult(1), ct)))
            .ParamName.ShouldBe("configure");
        (await Should.ThrowAsync<ArgumentNullException>(() =>
                TransactionalUnitOfWork.RunAsync(session.Db, configure, (Func<Task<int>>)null!, ct)))
            .ParamName.ShouldBe("work");
        (await Should.ThrowAsync<ArgumentNullException>(() =>
                TransactionalUnitOfWork.RunAsync(session.Db, configure, (Func<Task>)null!, ct)))
            .ParamName.ShouldBe("work");
        (await Should.ThrowAsync<ArgumentNullException>(() =>
                TransactionalUnitOfWork.RunAsync(null!, configure, () => Task.CompletedTask, ct)))
            .ParamName.ShouldBe("db");

        session.Transactions.Events.ShouldBeEmpty();
    }
}
