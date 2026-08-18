namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// Mutable, request-scoped implementation of <see cref="IActorContext"/>. Exactly one thing writes
/// <see cref="Actor"/>: <see cref="OrgScopedExecutor"/>, from the actor the unit of work was opened
/// with — it sets the value as the work begins and restores it as the work ends. Everything else
/// consumes it read-only through <see cref="IActorContext"/>.
/// <para>
/// Null means no unit of work is open, and since ADR-039 that is never a way to write: the audit
/// pass and the posting service both refuse an absent actor rather than stamping an unattributed
/// row. Do not assign this directly — a hand-written value is overwritten when the unit of work
/// opens, and the resulting silence is the failure this arrangement removes.
/// </para>
/// </summary>
public sealed class ActorContext : IActorContext
{
    public Actor? Actor { get; set; }
}
