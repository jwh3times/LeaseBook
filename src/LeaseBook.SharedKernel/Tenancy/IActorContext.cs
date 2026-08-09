namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// The user acting in the current unit of work (P52). Request-scoped: set by
/// <see cref="OrgScopedExecutor"/> from the <see cref="Actor"/> the unit of work declared — for a
/// request, the middleware resolves that actor from the authenticated user's id claim.
/// <see langword="null"/> means <b>no actor</b> — the seeder, the CLI and background jobs write as
/// the system, stamping a null <c>created_by</c> / <c>actor_user_id</c> (which must not throw).
/// Which system process that was is carried by <see cref="Actor.Reason"/> at the call site, since
/// the schema has no column for it. A bare <see cref="Guid"/> keeps <c>SharedKernel</c> free of an
/// Identity dependency; the host owns turning the claim into this id.
/// </summary>
public interface IActorContext
{
    Guid? UserId { get; }
}
