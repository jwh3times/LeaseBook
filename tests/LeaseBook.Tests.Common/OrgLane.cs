using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace LeaseBook.Tests.Common;

/// <summary>
/// A connection of its own to one organization. Two lanes run two units of work at once, as two
/// requests, or a request and a worker, do. Its context is set the way every test's is, through
/// <see cref="OrgScopedExecutor"/>. A module's lane derives from this and adds the services it builds
/// over <see cref="Db"/>, <see cref="Org"/> and <see cref="Actor"/>; <see cref="OrgLanes"/> runs two
/// of them against each other.
/// </summary>
public abstract class OrgLane<TLane> : IAsyncDisposable
    where TLane : OrgLane<TLane>
{
    private readonly OrgScopedExecutor _executor;
    private readonly Guid _orgId;
    private int _pid;

    protected OrgLane(PostgresFixture fixture, Guid orgId)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _orgId = orgId;
        Db = fixture.CreateContext(fixture.AppConnectionString, Org, Actor);
        _executor = new OrgScopedExecutor(Db, Org, Actor);
    }

    public AppDbContext Db { get; }

    /// <summary>The backend this lane's current unit of work runs on; zero before it has begun.</summary>
    public int Pid => Volatile.Read(ref _pid);

    /// <summary>The organization context <see cref="Db"/> reads. Services built over this lane take this one.</summary>
    protected OrgContext Org { get; } = new();

    /// <summary>The actor context <see cref="Db"/> reads. Services built over this lane take this one.</summary>
    protected ActorContext Actor { get; } = new();

    /// <summary>Runs <paramref name="work"/> in one organization transaction on this lane's connection.</summary>
    public async Task<T> RunAsync<T>(Func<TLane, Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        Db.ChangeTracker.Clear();
        return await _executor.RunAsSystemAsync(_orgId, "test-harness", async () =>
        {
            await using (var cmd = new NpgsqlCommand("SELECT pg_backend_pid()", (NpgsqlConnection)Db.Database.GetDbConnection(),
                (NpgsqlTransaction)Db.Database.CurrentTransaction!.GetDbTransaction()))
            {
                Volatile.Write(ref _pid, (int)(await cmd.ExecuteScalarAsync(ct))!);
            }

            return await work((TLane)this);
        }, ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/> and keeps its transaction open, holding every lock it took and
    /// every row it wrote uncommitted, until <see cref="HeldWork{T}.Commit"/> or
    /// <see cref="HeldWork{T}.Abort"/>. <paramref name="then"/>, if given, runs in the same
    /// transaction after the release and before the commit.
    /// </summary>
    public HeldWork<T> Hold<T>(Func<TLane, Task<T>> work, CancellationToken ct, Func<TLane, Task>? then = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (!await release.Task)
            {
                throw new HeldWorkAbortedException();
            }

            if (then is not null)
            {
                await then(lane);
            }

            return true;
        }, ct);
        return new(done.Task, run, release);
    }

    public virtual async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

/// <summary>A unit of work done and held open by <see cref="OrgLane{TLane}.Hold{T}"/>.</summary>
public sealed class HeldWork<T>
{
    private readonly Task _run;
    private readonly TaskCompletionSource<bool> _release;

    internal HeldWork(Task<T> done, Task run, TaskCompletionSource<bool> release)
    {
        Done = done;
        _run = run;
        _release = release;
    }

    /// <summary>The work's result. Its transaction is still open.</summary>
    public Task<T> Done { get; }

    public void Commit() => _release.TrySetResult(true);

    public void Abort() => _release.TrySetResult(false);

    /// <summary>Completes when the transaction has committed, or rolled back after <see cref="Abort"/>.</summary>
    public async Task Ended()
    {
        try
        {
            await _run;
        }
        catch (HeldWorkAbortedException)
        {
        }
    }
}

/// <summary>Rolls back a unit of work held by <see cref="OrgLane{TLane}.Hold{T}"/> when it is aborted.</summary>
public sealed class HeldWorkAbortedException : Exception
{
    public HeldWorkAbortedException()
        : base("The held unit of work was aborted.")
    {
    }

    public HeldWorkAbortedException(string message)
        : base(message)
    {
    }

    public HeldWorkAbortedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class OrgLanes
{
    /// <summary>
    /// Runs <paramref name="first"/> to the end of its work on lane <paramref name="a"/> and holds its
    /// transaction open; starts <paramref name="second"/> on lane <paramref name="b"/>; waits until the
    /// second has either finished or is waiting on a lock held against it; and only then commits the
    /// first.
    /// <para>
    /// That makes the interleaving the same every time, whichever way the code goes. Where the
    /// second takes a lock the first holds, it waits there and reads what the first committed. Where
    /// it takes none, it reads the state as it was before the first, and goes on either to finish on
    /// that or to wait at a row or key the first wrote, which it then writes over or collides with.
    /// Neither is decided by timing: nothing sleeps for the other to get anywhere.
    /// </para>
    /// </summary>
    public static async Task<(T1 First, T2? Second, Exception? SecondError)> RaceAsync<TLane, T1, T2>(
        PostgresFixture fixture, TLane a, TLane b, Func<TLane, Task<T1>> first, Func<TLane, Task<T2>> second,
        CancellationToken ct)
        where TLane : OrgLane<TLane>
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var held = a.Hold(first, ct);
        var firstResult = await held.Done;
        var running = b.RunAsync(second, ct);
        await WaitingOrDoneAsync(fixture, b, running, ct);
        held.Commit();
        await held.Ended();
        var (secondResult, error) = await OutcomeOf(running);
        return (firstResult, secondResult, error);
    }

    public static async Task<(T? Result, Exception? Error)> OutcomeOf<T>(Task<T> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            return (await task, null);
        }
        catch (Exception e)
        {
            return (default, e);
        }
    }

    public static async Task<Exception?> ErrorOf(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            await task;
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>
    /// Waits until the lane's unit of work has finished or its backend is waiting on a lock it has
    /// not been granted: an advisory lock, a row another transaction wrote, or a key another
    /// transaction inserted. Returns true for waiting. Polls the database; never waits on time alone.
    /// </summary>
    public static async Task<bool> WaitingOrDoneAsync<TLane>(
        PostgresFixture fixture, OrgLane<TLane> lane, Task running, CancellationToken ct)
        where TLane : OrgLane<TLane>
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(running);
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
