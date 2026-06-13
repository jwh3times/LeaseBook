namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// The user acting in the current unit of work (P52). Request-scoped: set by the HTTP middleware from
/// the authenticated user's id claim, alongside the org resolution. <see langword="null"/> means
/// <b>no actor</b> — the seeder and background jobs write as the system, stamping a null
/// <c>created_by</c> / <c>actor_user_id</c> (which must not throw). A bare <see cref="Guid"/> keeps
/// <c>SharedKernel</c> pure (no Identity dependency); the host owns turning the claim into this id.
/// </summary>
public interface IActorContext
{
    Guid? UserId { get; }
}
