namespace LeaseBook.Modules.Capabilities.Contracts;

/// <summary>
/// Consumer-owned port (ADR-007) for opening the platform plane. The implementation is
/// <c>PlatformScopedExecutor</c>, which lives in the host, so the seam has to be an interface here
/// plus a thin host adapter.
/// <para>
/// The reason the implementation lives in the host is <b>containment, not purity</b>. Its sibling
/// <c>OrgScopedExecutor</c> is a scoped executor living in <c>SharedKernel</c>, so "SharedKernel
/// stays pure cross-cutting primitives" cannot be the reason — every feature module references
/// <c>SharedKernel</c>, so a platform executor there would be nameable from every module. Keeping it
/// in the host makes it nameable from none, and this port plus its adapter is the only way through.
/// That is what holds <c>app.platform</c> to the one auditable call site ADR-028 depends on.
/// </para>
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

    /// <summary>
    /// Value-returning form. Prefer this whenever the scoped work produces something: the void form
    /// forces callers into a captured local plus a "this cannot happen" null check, which is a
    /// branch the type system should be removing rather than the caller asserting.
    /// </summary>
    Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default);
}
