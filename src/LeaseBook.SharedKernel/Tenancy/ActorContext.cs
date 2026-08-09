namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// Mutable, request-scoped implementation of <see cref="IActorContext"/>. Exactly one thing writes
/// <see cref="UserId"/>: <see cref="OrgScopedExecutor"/>, from the <see cref="Actor"/> the unit of
/// work was opened with — it sets the value as the work begins and restores it as the work ends.
/// Everything else consumes it read-only through <see cref="IActorContext"/>.
/// <para>
/// Null still means a system write, as it always did. What changed is that the null now comes from a
/// caller saying <see cref="Actor.System"/> with a named reason rather than from nobody having set
/// anything. Do not assign this directly: a hand-written value is overwritten when the unit of work
/// opens, and the resulting silence is the failure this arrangement removes.
/// </para>
/// </summary>
public sealed class ActorContext : IActorContext
{
    public Guid? UserId { get; set; }
}
