using LeaseBook.Modules.Operations.Domain;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// Transaction-scoped serialization seam used by <see cref="RunEngine"/>.
/// <para>
/// <b>It lives here rather than in <c>Contracts/</c>, and that is deliberate (F-c).</b> That folder
/// has a precise meaning — consumer-owned ports another module satisfies through a host adapter
/// (ADR-007), which is what all ten interfaces in it are. This one is satisfied by
/// <see cref="RunPeriodLock"/> in this same file, registered by Operations' own DI extension, and it
/// reaches Postgres directly rather than crossing a module boundary at all. Filing it beside the ten
/// would say "another module owns this", which is the one thing it does not mean, and would blur the
/// boundary <c>ModuleBoundaryTests</c> exists to keep sharp.
/// </para>
/// <para>
/// The rule, generally: an interface goes in <c>Contracts/</c> because something OUTSIDE the module
/// implements it — not because it is an interface.
/// </para>
/// </summary>
public interface IRunPeriodLock
{
    Task AcquireAsync(RunType runType, RunPeriod period, CancellationToken ct);
}

/// <summary>
/// Serializes confirms for one <c>(org, run type, period)</c> on the caller's ambient PostgreSQL
/// transaction. The lock is transaction-scoped, so commit, rollback, cancellation, and connection
/// failure all release it without a cleanup path in application code.
/// <para>
/// PostgreSQL advisory locks take integers rather than a composite value. <c>hashtextextended</c>
/// turns the explicit organization context plus run coordinates into one stable 64-bit key. A theoretical
/// hash collision can only serialize unrelated runs; it cannot let related runs overlap or change
/// correctness.
/// </para>
/// </summary>
internal sealed class RunPeriodLock(DbContext db) : IRunPeriodLock
{
    public async Task AcquireAsync(RunType runType, RunPeriod period, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"""
             SELECT pg_advisory_xact_lock(
                 hashtextextended(
                     concat_ws(':', 'lb:run', current_setting('app.org_id', true),
                         {runType.ToString()}, {period.Year}, {period.Month}),
                     0))
             """,
            ct);
    }
}
