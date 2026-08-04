namespace LeaseBook.Web.Observability;

/// <summary>
/// Stable <see cref="EventId"/>s for structured logs. Track B's B4 alert rules key on these —
/// never renumber an existing id; add new ones at the end of the range.
/// 1000-1099 = host/error plumbing. 1100+ reserved for domain areas.
/// </summary>
public static class LogEvents
{
    public static readonly EventId UnhandledException = new(1000, nameof(UnhandledException));
    public static readonly EventId DomainRejection = new(1001, nameof(DomainRejection));
    public static readonly EventId ValidationRejection = new(1002, nameof(ValidationRejection));
    public static readonly EventId ImportRowFailed = new(1003, nameof(ImportRowFailed));

    // 1100-1199 = import correction / supersede (WP-7). First domain-area block per the 1100+ rule.
    public static readonly EventId SupersedeReversalRace = new(1100, nameof(SupersedeReversalRace));

    /// <summary>A pm_income opening position violated the held-fees shape at post time (WP-7 Task 10 /
    /// ADR-020 §5); the row surfaces as a row error, never a 500.</summary>
    public static readonly EventId HeldFeesShapeRejected = new(1101, nameof(HeldFeesShapeRejected));

    // 1200-1299 = scheduled jobs (WP-11). ADR-025 reserved this taxonomy for the Hangfire sweep
    // rather than letting a background job invent its own: a job has no HttpContext and therefore no
    // correlation id, so these ids are the only stable handle an alert rule can key on.

    /// <summary>A trust-accounting invariant (§C.7) failed for one org during the sweep. This is the
    /// event Track B's B4 alert rule pages on — fiduciary incorrectness, never routine noise.</summary>
    public static readonly EventId InvariantViolation = new(1200, nameof(InvariantViolation));

    /// <summary>The nightly sweep finished with no violations. Its absence is itself a signal: a
    /// silent night means the job did not run.</summary>
    public static readonly EventId InvariantSweepCompleted = new(1201, nameof(InvariantSweepCompleted));

    // 1300-1399 = platform capabilities (ADR-028). Its own block rather than an extension of the
    // 1100 import block or the 1200 job block, per the 1100+ convention: a capability event can
    // originate on the HTTP surface or in a job, so it belongs to neither.

    /// <summary>A run confirm was rejected because the capability set moved after its preview. Expected
    /// and recoverable — the operator re-previews. A SUSTAINED rate of these is the signal worth acting
    /// on: it means something is flipping capabilities under live operators, or replicas disagree.</summary>
    public static readonly EventId CapabilityVersionConflict = new(1300, nameof(CapabilityVersionConflict));
}
