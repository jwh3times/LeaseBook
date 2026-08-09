using Microsoft.EntityFrameworkCore;

namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// The transaction bracket both scoping planes share: open one transaction, let the caller set
/// whatever transaction-local context it needs, run the work, commit — rolling back on failure.
/// <para>
/// It deliberately knows <b>no GUC name</b>. Each plane's executor supplies its own
/// <c>set_config</c> through the <c>configure</c> callback. That is not incidental: it is what keeps
/// <c>app.platform</c> to the single call site <c>PlatformScopeCallSiteTests</c> polices (ADR-028).
/// This type hands no caller a typed way to open the platform escape — reaching it still means
/// writing the <c>set_config</c> string yourself, which is exactly what the scan looks for. A
/// primitive that took a GUC name and value would have put that escape in <c>SharedKernel</c>, where
/// every feature module could reach it.
/// </para>
/// <para>
/// <b>Refuses to nest.</b> Every context setter opens its own transaction and sets its GUC with
/// <c>SET LOCAL</c>, so an inner unit of work on the same <see cref="DbContext"/> is always a
/// mistake. The provider already rejects it, but with a message about connections that says nothing
/// about tenancy; this check names the actual constraint and points at the two legitimate shapes —
/// out-of-band work resolves its own scope (and therefore its own <see cref="DbContext"/>), and
/// in-request work reads on the ambient transaction rather than opening a second one. Nesting across
/// <i>different</i> contexts is the correct pattern and is deliberately not caught here: that is how
/// <c>CapabilityCache</c> refreshes while a request transaction is open elsewhere.
/// </para>
/// </summary>
public static class TransactionalUnitOfWork
{
    /// <summary>
    /// Runs <paramref name="work"/> in one transaction, applying <paramref name="configure"/> inside
    /// it first, and returns the work's result.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        DbContext db,
        Func<CancellationToken, Task> configure,
        Func<Task<T>> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(work);

        if (db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "A scoped unit of work cannot nest: this DbContext already has a transaction open. " +
                "Context is set with SET LOCAL and dies with the transaction that set it, so an " +
                "inner unit of work would be operating under the outer transaction's context, not " +
                "its own. Out-of-band work (a job, the CLI, a cache refresh) must resolve its own " +
                "scope so it gets its own DbContext; in-request work must read on the ambient " +
                "transaction instead of opening a second one.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await configure(ct);

            var result = await work();

            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>Void-returning form of <see cref="RunAsync{T}"/>.</summary>
    public static async Task RunAsync(
        DbContext db,
        Func<CancellationToken, Task> configure,
        Func<Task> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await RunAsync<object?>(
            db,
            configure,
            async () =>
            {
                await work();
                return null;
            },
            ct);
    }
}
