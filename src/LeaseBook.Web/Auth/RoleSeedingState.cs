namespace LeaseBook.Web.Auth;

/// <summary>
/// Per-replica record of whether the four fixed roles have been seeded. Singleton, because it is
/// replica state: <see cref="RoleSeedingProbe"/> writes it and
/// <c>RoleSeedingReadinessCheck</c> reads it from request threads.
/// <para>
/// <b>Why a readiness gate and not just a log line.</b> The roles are a hard precondition for
/// sign-in and for every role-based policy. Guarding role seeding against an unreachable database
/// without this would be strictly worse than crashing: the host would bind, the capability readiness
/// check would go healthy the moment the seam became reachable, and a replica with no roles would
/// enter rotation and fail every authenticated request — a silent partial outage in place of a loud
/// crash loop. This is the gate that makes the guard safe (ADR-028 §9).
/// </para>
/// <para>
/// <b>Never cleared once set</b>, matching <c>CapabilityCache.IsPopulated</c>. Roles are
/// append-only in practice and a later database blip does not un-create them, so re-proving would
/// only let readiness flap and evict a replica that is still serving correctly. The container's
/// liveness probe deliberately depends on that: it targets the static <c>/api/health</c> precisely
/// because readiness must never restart a container for a dependency reason.
/// </para>
/// </summary>
public sealed class RoleSeedingState
{
    private volatile bool _seeded;
    private long _attempts;

    /// <summary>
    /// True once all four roles are known to exist on this replica. <b>Volatile</b>: written from the
    /// probe's background task and read from request threads, so without it the JIT is free to hoist
    /// the read and a replica could report not-ready indefinitely after seeding had already
    /// succeeded.
    /// </summary>
    public bool IsSeeded => _seeded;

    /// <summary>Seeding attempts made, successful or not. Diagnostic only.</summary>
    public long Attempts => Interlocked.Read(ref _attempts);

    /// <summary>Records that the roles now exist. Idempotent.</summary>
    public void MarkSeeded() => _seeded = true;

    /// <summary>Counts one attempt and returns the new total.</summary>
    public long RecordAttempt() => Interlocked.Increment(ref _attempts);

    /// <summary>
    /// Test-only: returns this to its cold-start state. Used to prove the readiness endpoint can
    /// answer 503 for a role-seeding reason specifically — a gate that can only ever answer 200 is
    /// not a gate.
    /// </summary>
    internal void ResetForTesting() => _seeded = false;
}
