using LeaseBook.Modules.Capabilities.Contracts;
using LeaseBook.Web.Tenancy;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter for the Capabilities module's <see cref="IPlatformScope"/> port (ADR-007 / ADR-028).
/// The module cannot reference <see cref="PlatformScopedExecutor"/> directly — that lives in the host,
/// and the module boundary is assembly-level — so the port is declared in the module and satisfied
/// here. Nothing is added on the way through: keeping the executor the only implementation is what
/// keeps <c>PlatformScopeCallSiteTests</c>' single-escape rule meaningful.
/// </summary>
internal sealed class PlatformScopeAdapter(PlatformScopedExecutor executor) : IPlatformScope
{
    public Task RunAsync(Func<Task> work, CancellationToken ct = default) => executor.RunAsync(work, ct);

    public Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default) =>
        executor.RunAsync(work, ct);
}
