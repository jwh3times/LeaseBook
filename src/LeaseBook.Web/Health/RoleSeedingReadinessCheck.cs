using LeaseBook.Web.Auth;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LeaseBook.Web.Health;

/// <summary>
/// Surfaces <see cref="RoleSeedingState.IsSeeded"/> to the container readiness probe (ADR-028 §9).
/// <para>
/// <b>This is the half that makes guarding role seeding safe.</b> Role seeding no longer kills the
/// process when the database is unreachable, so the host binds — and without this check the only
/// readiness signal would be <see cref="CapabilityReadinessCheck"/>, which asks a different question
/// entirely. The seam can be reachable while the roles are still missing (they are seeded once, at
/// boot; the seam is probed continuously), so a replica would report healthy, enter rotation, and
/// fail every authenticated request. That trades a loud crash loop for a silent partial outage, which
/// is the worse of the two.
/// </para>
/// <para>
/// <b>Registered separately from the capability check rather than folded into it</b> so the readiness
/// payload names which precondition is missing. Both carry <see cref="CapabilityReadinessCheck.ReadyTag"/>
/// and <c>/api/health/ready</c> reports the worst of the two, so the endpoint is 503 until both hold.
/// </para>
/// </summary>
public sealed class RoleSeedingReadinessCheck(RoleSeedingState state) : IHealthCheck
{
    /// <summary>The registration name, reported in the readiness payload.</summary>
    public const string Name = "role-seeding";

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.IsSeeded
            ? HealthCheckResult.Healthy("the fixed roles are seeded")
            : HealthCheckResult.Unhealthy(
                "the fixed roles are not seeded yet, so sign-in and every role-based policy would fail"));
}
