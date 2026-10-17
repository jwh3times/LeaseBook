using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace LeaseBook.Tests.Accounting.Support;

/// <summary>
/// A connection of its own to one organization, with the posting services built over it the way
/// <see cref="AccountingTestHarness"/> builds them over a scope. Its context is set the way every
/// test's is, through <see cref="OrgScopedExecutor"/>. Two lanes run two units of work at once, as two
/// requests do.
/// </summary>
internal sealed class Lane : IAsyncDisposable
{
    private readonly OrgContext _org = new();
    private readonly ActorContext _actor = new();
    private readonly OrgScopedExecutor _executor;
    private readonly Guid _orgId;
    private int _pid;

    public Lane(PostgresFixture fixture, Guid orgId)
    {
        _orgId = orgId;
        Db = fixture.CreateContext(fixture.AppConnectionString, _org, _actor);
        _executor = new OrgScopedExecutor(Db, _org, _actor);
    }

    public AppDbContext Db { get; }

    public PostingService Posting =>
        new(Db, _org, new AccountingPeriods(Db), _actor, reconciliationLock: new ReconciliationLock(Db));

    public AccountingEventService Events => new(Db, Posting, new PostingLock(Db, _org));

    public ReversalService Reversal => new(Db, _org, Posting);

    /// <summary>The backend this lane's current unit of work runs on; zero before it has begun.</summary>
    public int Pid => Volatile.Read(ref _pid);

    /// <summary>Runs <paramref name="work"/> in one organization transaction on this lane's connection.</summary>
    public async Task<T> RunAsync<T>(Func<Lane, Task<T>> work, CancellationToken ct)
    {
        Db.ChangeTracker.Clear();
        return await _executor.RunAsSystemAsync(_orgId, "test-harness", async () =>
        {
            await using (var cmd = new NpgsqlCommand("SELECT pg_backend_pid()", (NpgsqlConnection)Db.Database.GetDbConnection(),
                (NpgsqlTransaction)Db.Database.CurrentTransaction!.GetDbTransaction()))
            {
                Volatile.Write(ref _pid, (int)(await cmd.ExecuteScalarAsync(ct))!);
            }

            return await work(this);
        }, ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/> and keeps its transaction open, holding every lock it took and
    /// every row it wrote uncommitted, until <see cref="HeldWork{T}.Commit"/>.
    /// </summary>
    public HeldWork<T> Hold<T>(Func<Lane, Task<T>> work, CancellationToken ct)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = RunAsync(async lane =>
        {
            T result;
            try
            {
                result = await work(lane);
            }
            catch (Exception e)
            {
                done.TrySetException(e);
                throw;
            }

            done.TrySetResult(result);
            await release.Task;
            return true;
        }, ct);
        return new(done.Task, run, release);
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

/// <summary>A unit of work done and held open by <see cref="Lane.Hold{T}"/>.</summary>
internal sealed class HeldWork<T>(Task<T> done, Task run, TaskCompletionSource release)
{
    /// <summary>The work's result. Its transaction is still open.</summary>
    public Task<T> Done { get; } = done;

    public void Commit() => release.TrySetResult();

    /// <summary>Completes when the transaction has committed.</summary>
    public Task Ended() => run;
}

/// <summary>What became of two units of work run by <see cref="Lanes.RaceAsync{T1, T2}"/>.</summary>
internal sealed record Race<T1, T2>(T1 First, T2? Second, Exception? SecondError);

internal static class Lanes
{
    /// <summary>
    /// Runs <paramref name="first"/> to the end of its work on one connection and holds its
    /// transaction open; starts <paramref name="second"/> on another; waits until the second has
    /// either finished or is waiting on a lock held against it; and only then commits the first.
    /// <para>
    /// That makes the interleaving the same every time, whichever way the code goes. Where the
    /// second takes a lock the first holds, it waits there and reads what the first committed. Where
    /// it takes none, it reads the state as it was before the first, and goes on either to finish on
    /// that or to wait at a key the first inserted, which it then collides with. Neither is decided
    /// by timing: nothing sleeps for the other to get anywhere.
    /// </para>
    /// </summary>
    public static async Task<Race<T1, T2>> RaceAsync<T1, T2>(
        PostgresFixture fixture, OrgScope scope, Func<Lane, Task<T1>> first, Func<Lane, Task<T2>> second,
        CancellationToken ct)
    {
        await using var a = new Lane(fixture, scope.OrgId);
        await using var b = new Lane(fixture, scope.OrgId);
        var held = a.Hold(first, ct);
        var firstResult = await held.Done;
        var running = b.RunAsync(second, ct);
        await WaitingOrDoneAsync(fixture, b, running, ct);
        held.Commit();
        await held.Ended();

        T2? secondResult = default;
        Exception? error = null;
        try
        {
            secondResult = await running;
        }
        catch (Exception e)
        {
            error = e;
        }

        scope.Db.ChangeTracker.Clear();
        return new(firstResult, secondResult, error);
    }

    /// <summary>
    /// Waits until the lane's unit of work has finished or its backend is waiting on a lock it has
    /// not been granted: an advisory lock, or a key another transaction inserted. Returns true for
    /// waiting. Polls the database; never waits on time alone.
    /// </summary>
    private static async Task<bool> WaitingOrDoneAsync(PostgresFixture fixture, Lane lane, Task running, CancellationToken ct)
    {
        await using var monitor = await fixture.OpenAppConnectionAsync(ct);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!running.IsCompleted)
        {
            if (lane.Pid != 0)
            {
                await using var waiting = new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = @pid AND NOT granted)", monitor);
                waiting.Parameters.AddWithValue("pid", lane.Pid);
                if ((bool)(await waiting.ExecuteScalarAsync(ct))!)
                {
                    return true;
                }
            }

            if (clock.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("The second unit of work neither finished nor waited on a lock within 30 seconds.");
            }

            await Task.WhenAny(running, Task.Delay(5, ct));
        }

        return false;
    }
}
