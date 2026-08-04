using LeaseBook.Modules.Capabilities.Caching;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LeaseBook.Web.Health;

/// <summary>
/// Surfaces <see cref="CapabilityCache.IsPopulated"/> to the container readiness probe (ADR-028).
/// <para>
/// <b>Why readiness and not just a log line.</b> "Serve the last snapshot" is undefined when there is
/// no last snapshot, and cold starts are routine here: dev runs <c>minReplicas: 0</c> and prod scales
/// 1→5. A replica that booted while Postgres was degraded would answer every capability question with
/// the registry defaults — "everything off" — while its siblings answered correctly, so the same
/// request would succeed or fail depending on which replica took it. Staying out of rotation until the
/// seam is reachable is the only way that is not silently non-deterministic.
/// </para>
/// <para>
/// <b>It reads the cache, never <c>ICapabilityGate</c>.</b> The gate is scoped and requires ambient org
/// context; an unauthenticated probe has none, so asking it would throw and report the seam broken when
/// it is fine. <see cref="CapabilityCache"/> is a singleton, so this check holds it directly.
/// </para>
/// <para>
/// <b><see cref="CapabilityCache.IsPopulated"/> means "the seam is reachable", not "some org's set is
/// cached"</b> — see its documentation. Gating on a cached set would deadlock a fresh replica: not-ready
/// → no traffic → nothing resolved → not-ready forever. <c>CapabilityReadinessProbe</c> establishes
/// reachability out of band, independent of inbound traffic; this check only reports what it found.
/// </para>
/// </summary>
public sealed class CapabilityReadinessCheck(CapabilityCache cache) : IHealthCheck
{
    /// <summary>The registration name, and the tag the readiness endpoint filters on.</summary>
    public const string Name = "capability-seam";

    /// <summary>
    /// Endpoints tagged with this are readiness, not liveness: an unreachable capability seam must
    /// take the replica out of rotation, never restart a process that is otherwise serving fine.
    /// </summary>
    public const string ReadyTag = "ready";

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(cache.IsPopulated
            ? HealthCheckResult.Healthy("capability seam reachable")
            : HealthCheckResult.Unhealthy("capability cache not yet loaded"));
}
