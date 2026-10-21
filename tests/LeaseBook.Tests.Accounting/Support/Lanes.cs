using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.Tests.Common;

namespace LeaseBook.Tests.Accounting.Support;

/// <summary>
/// A connection of its own to one organization (<see cref="OrgLane{TLane}"/>), with the posting
/// services built over it the way <see cref="AccountingTestHarness"/> builds them over a scope.
/// </summary>
internal sealed class Lane(PostgresFixture fixture, Guid orgId) : OrgLane<Lane>(fixture, orgId)
{
    public PostingService Posting =>
        new(Db, Org, new AccountingPeriods(Db), Actor, reconciliationLock: new ReconciliationLock(Db));

    public AccountingEventService Events => new(Db, Posting, new PostingLock(Db, Org));

    public ReversalService Reversal => new(Db, Org, Posting);

    /// <summary>The organization's posting lock, taken on this lane's connection.</summary>
    public PostingLock Lock => new(Db, Org);

    /// <summary>The real dispatcher over this lane's connection (<see cref="AccountingSender"/>).</summary>
    public ISender Sender(ITenantPostingDimensions dimensions) => AccountingSender.Create(Db, Org, Actor, dimensions);
}

/// <summary>What became of two units of work run by <see cref="Lanes.RaceAsync{T1, T2}"/>.</summary>
internal sealed record Race<T1, T2>(T1 First, T2? Second, Exception? SecondError);

/// <summary>What became of two units of work run by <see cref="Lanes.UnderHeldLockAsync{T1, T2}"/>.</summary>
internal sealed record Interleaving<T1, T2>(
    bool SecondWaited, T1? First, Exception? FirstError, T2? Second, Exception? SecondError);

internal static class Lanes
{
    /// <summary>Runs the two units of work as <see cref="OrgLanes.RaceAsync"/> does, on two new lanes.</summary>
    public static async Task<Race<T1, T2>> RaceAsync<T1, T2>(
        PostgresFixture fixture, OrgScope scope, Func<Lane, Task<T1>> first, Func<Lane, Task<T2>> second,
        CancellationToken ct)
    {
        await using var a = new Lane(fixture, scope.OrgId);
        await using var b = new Lane(fixture, scope.OrgId);
        var (firstResult, secondResult, error) = await OrgLanes.RaceAsync(fixture, a, b, first, second, ct);
        scope.Db.ChangeTracker.Clear();
        return new(firstResult, secondResult, error);
    }

    /// <summary>
    /// Takes the organization's posting lock on one connection; starts <paramref name="second"/> on
    /// another; waits until the second has either finished or is waiting on a lock; and only then runs
    /// <paramref name="first"/>, on the connection that already holds the lock, and commits it.
    /// <para>
    /// Where <see cref="RaceAsync{T1, T2}"/> shows what the second reads, this shows what it writes
    /// before it waits: anything it has written by then, the first meets while holding the lock the
    /// second is waiting on. Both units of work are awaited to the end, whatever becomes of either, so
    /// neither connection is closed under work still running on it.
    /// </para>
    /// </summary>
    public static async Task<Interleaving<T1, T2>> UnderHeldLockAsync<T1, T2>(
        PostgresFixture fixture, OrgScope scope, Func<Lane, Task<T1>> first, Func<Lane, Task<T2>> second,
        CancellationToken ct)
    {
        await using var a = new Lane(fixture, scope.OrgId);
        await using var b = new Lane(fixture, scope.OrgId);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRun = a.RunAsync(async lane =>
        {
            await lane.Lock.AcquireAsync(ct);
            locked.TrySetResult();
            await go.Task;
            return await first(lane);
        }, ct);

        Task<T2>? secondRun = null;
        var waited = false;
        try
        {
            await Task.WhenAny(locked.Task, firstRun);
            if (locked.Task.IsCompleted)
            {
                secondRun = b.RunAsync(second, ct);
                waited = await OrgLanes.WaitingOrDoneAsync(fixture, b, secondRun, ct);
            }
        }
        finally
        {
            go.TrySetResult();
        }

        var (firstResult, firstError) = await OrgLanes.OutcomeOf(firstRun);
        var (secondResult, secondError) = secondRun is null ? (default, null) : await OrgLanes.OutcomeOf(secondRun);
        scope.Db.ChangeTracker.Clear();
        return new(waited, firstResult, firstError, secondResult, secondError);
    }
}
