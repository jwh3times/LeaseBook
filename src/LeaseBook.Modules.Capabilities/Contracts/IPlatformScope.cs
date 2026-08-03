namespace LeaseBook.Modules.Capabilities.Contracts;

/// <summary>
/// Consumer-owned port (ADR-007) for opening the platform plane. The implementation is
/// <c>PlatformScopedExecutor</c>, which lives in the host — a module may not reference
/// <c>LeaseBook.Web</c>, and <c>SharedKernel</c> stays pure cross-cutting primitives, so the seam has
/// to be an interface here plus a thin host adapter.
/// <para>
/// <b>This opens its own transaction and therefore cannot nest inside an in-flight request
/// transaction.</b> Only out-of-band work — the cache refresh, the platform CLI — may use it. The
/// in-request resolution path (Task 6) deliberately does NOT: it reads <c>feature_flags</c> (globally
/// readable) and the caller's own <c>entitlements</c>/<c>capability_cohorts</c> rows (readable under
/// the ambient <c>app.org_id</c> via each table's <c>_org_read</c> policy), which is exactly what
/// makes durable in-transaction resolution possible without a second escape.
/// </para>
/// </summary>
public interface IPlatformScope
{
    /// <summary>
    /// Runs <paramref name="work"/> inside one transaction with <c>app.platform</c> set
    /// transaction-locally, committing on success and rolling back on failure.
    /// </summary>
    Task RunAsync(Func<Task> work, CancellationToken ct = default);
}
