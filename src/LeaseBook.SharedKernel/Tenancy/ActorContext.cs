namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// Mutable, request-scoped implementation of <see cref="IActorContext"/>. Only the HTTP unit-of-work
/// middleware writes <see cref="UserId"/> (from the auth claim); everything else consumes it read-only
/// through <see cref="IActorContext"/>. Left null for the seeder and background jobs (system writes).
/// </summary>
public sealed class ActorContext : IActorContext
{
    public Guid? UserId { get; set; }
}
