using LeaseBook.Modules.Operations.Domain;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Operations.Runs;

/// <summary>Transaction-scoped serialization seam used by <see cref="RunEngine"/>.</summary>
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
/// turns the explicit org context plus run coordinates into one stable 64-bit key. A theoretical
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
