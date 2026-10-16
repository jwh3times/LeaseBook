using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LeaseBook.Tests.Payments.Support;

/// <summary>
/// A connection of its own to the harness's organization, with the two engines and a recording
/// ledger over it. Its context is set the way every test's is, through <see cref="OrgScopedExecutor"/>.
/// Two lanes run two units of work at once, as two requests or a request and the worker do.
/// </summary>
internal sealed class Lane : IAsyncDisposable
{
    private readonly ServiceProvider _accounting;
    private readonly Lazy<ServiceProvider> _refundChecks;
    private readonly OrgScopedExecutor _executor;
    private readonly Guid _orgId;
    private int _pid;

    public Lane(PostgresFixture fixture, Guid orgId, Guid bankId, Guid ownerId, TimeProvider clock,
        IPaymentEligibility eligibility, IPaymentFeeRules feeRules)
    {
        _orgId = orgId;
        var org = new OrgContext();
        var actor = new ActorContext();
        Db = fixture.CreateContext(fixture.AppConnectionString, org, actor);
        _executor = new OrgScopedExecutor(Db, org, actor);
        _accounting = PaymentsHarness.AccountingOver(Db, org, actor);
        _refundChecks = new(() => RefundCheckHarness.ServicesOver(Db, org, actor, clock));
        Ledger = new RecordingLedger(_accounting.GetRequiredService<IAccountingEvents>(), ownerId, bankId);
        Payments = new PaymentEngine(Db, org, clock, Ledger, eligibility, feeRules);
        Settlements = new SettlementEngine(Db, clock, Payments, Ledger);
    }

    /// <summary>A lane for refund-check work, which uses none of the payment engines' stand-ins.</summary>
    public Lane(PostgresFixture fixture, Guid orgId, TimeProvider clock)
        : this(fixture, orgId, Guid.Empty, Guid.Empty, clock, new StubEligibility(), new StubFeeRules())
    {
    }

    public AppDbContext Db { get; }

    /// <summary>
    /// The refund-check commands and queries over this lane's connection, with the real host ledger
    /// adapter and Accounting behind them (see <see cref="RefundCheckHarness.ServicesOver"/>).
    /// </summary>
    public ISender Sender => _refundChecks.Value.GetRequiredService<ISender>();

    public PaymentEngine Payments { get; }

    public SettlementEngine Settlements { get; }

    public RecordingLedger Ledger { get; }

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
    /// every row it wrote uncommitted, until <see cref="HeldWork{T}.Commit"/> or
    /// <see cref="HeldWork{T}.Abort"/>. <paramref name="then"/>, if given, runs in the same
    /// transaction after the release and before the commit.
    /// </summary>
    public HeldWork<T> Hold<T>(Func<Lane, Task<T>> work, CancellationToken ct, Func<Lane, Task>? then = null)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = RunAsync(async lane =>
        {
            T result;
            try { result = await work(lane); }
            catch (Exception e) { done.TrySetException(e); throw; }
            done.TrySetResult(result);
            if (!await release.Task) { throw new HeldWorkAborted(); }
            if (then is not null) { await then(lane); }
            return true;
        }, ct);
        return new(done.Task, run, release);
    }

    /// <summary>
    /// Holds, uncommitted, a notice row under <paramref name="eventId"/> as if another delivery of it
    /// were being stored. A <see cref="PaymentEngine.ReceiveAsync"/> of that event takes its event and
    /// reference locks and then waits at its own insert for this one to end: it stands still at the
    /// point between storing a fact and waking the payment, which nothing else can hold it at.
    /// </summary>
    public HeldWork<bool> HoldNotice(FixtureBinding binding, string eventId, CancellationToken ct) => Hold(async lane =>
    {
        lane.Db.Add(new PaymentObservation
        {
            Id = UuidV7.NewId(),
            EventId = eventId,
            ProviderId = "held-notice",
            Generation = binding.Generation,
            Account = binding.Account,
            Mode = binding.Mode,
            Kind = "Processing",
            BankId = binding.BankId,
            ObservedAt = DateTime.UtcNow,
            Fingerprint = new string('0', 64),
        });
        await lane.Db.SaveChangesAsync(ct);
        return true;
    }, ct);

    public async ValueTask DisposeAsync()
    {
        if (_refundChecks.IsValueCreated) { await _refundChecks.Value.DisposeAsync(); }
        await _accounting.DisposeAsync();
        await Db.DisposeAsync();
    }
}

/// <summary>A unit of work done and held open by <see cref="Lane.Hold{T}"/>.</summary>
internal sealed class HeldWork<T>(Task<T> done, Task run, TaskCompletionSource<bool> release)
{
    /// <summary>The work's result. Its transaction is still open.</summary>
    public Task<T> Done { get; } = done;

    public void Commit() => release.TrySetResult(true);

    public void Abort() => release.TrySetResult(false);

    /// <summary>Completes when the transaction has committed, or rolled back after <see cref="Abort"/>.</summary>
    public async Task Ended()
    {
        try { await run; }
        catch (HeldWorkAborted) { }
    }
}

internal sealed class HeldWorkAborted : Exception;

/// <summary>What became of two units of work run by <see cref="PaymentsHarness.RaceAsync{T1, T2}"/>.</summary>
internal sealed record Race<T1, T2>(T1 First, T2? Second, Exception? SecondError, RecordingLedger FirstLedger, RecordingLedger SecondLedger)
{
    public int SettlementRequests => FirstLedger.Settlements.Count + SecondLedger.Settlements.Count;
}

internal static class Lanes
{
    public static async Task<(T? Result, Exception? Error)> OutcomeOf<T>(Task<T> task)
    {
        try { return (await task, null); }
        catch (Exception e) { return (default, e); }
    }

    public static async Task<Exception?> ErrorOf(Task task)
    {
        try { await task; return null; }
        catch (Exception e) { return e; }
    }

    /// <summary>
    /// Waits until the lane's unit of work has finished or its backend is waiting on a lock it has
    /// not been granted: an advisory lock, a row another transaction wrote, or a key another
    /// transaction inserted. Returns true for waiting. Polls the database; never waits on time alone.
    /// </summary>
    public static async Task<bool> WaitingOrDoneAsync(PostgresFixture fixture, Lane lane, Task running, CancellationToken ct)
    {
        await using var monitor = await fixture.OpenAppConnectionAsync(ct);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!running.IsCompleted)
        {
            if (lane.Pid != 0)
            {
                await using var waiting = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid = @pid AND NOT granted)", monitor);
                waiting.Parameters.AddWithValue("pid", lane.Pid);
                if ((bool)(await waiting.ExecuteScalarAsync(ct))!) { return true; }
            }
            if (clock.Elapsed > TimeSpan.FromSeconds(30))
            { throw new TimeoutException("The second unit of work neither finished nor waited on a lock within 30 seconds."); }
            await Task.WhenAny(running, Task.Delay(5, ct));
        }
        return false;
    }
}
