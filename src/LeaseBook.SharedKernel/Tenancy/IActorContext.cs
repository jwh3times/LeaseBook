namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// Who is accountable for the current unit of work (P52). Request-scoped: set by
/// <see cref="OrgScopedExecutor"/> from the <see cref="Tenancy.Actor"/> the unit of work declared —
/// for a request, the middleware resolves that actor from the authenticated user's id claim.
/// <para>
/// <see langword="null"/> means <b>no unit of work is open</b>, not "the system did it". Since
/// ADR-039 every write carries a durable actor, so a null here is a programming error on any path
/// that is about to write: <c>AppDbContext</c> and the posting service both refuse rather than
/// stamping an unattributed row. The seeder, the CLI and background jobs are system actors with a
/// named process, which is a value — not an absence.
/// </para>
/// <para>
/// A bare <see cref="Guid"/> in <see cref="UserId"/> keeps <c>SharedKernel</c> free of an Identity
/// dependency; the host owns turning the claim into that id.
/// </para>
/// </summary>
public interface IActorContext
{
    /// <summary>The declared actor, or <see langword="null"/> outside any unit of work.</summary>
    Actor? Actor { get; }

    /// <summary>
    /// The acting <i>human</i>, for domain fields that record one — who signed off a verification,
    /// who finalized a reconciliation. Null for a system actor, which is a meaningful answer for
    /// those fields. Audit and journal attribution uses <see cref="Actor"/> instead, because null
    /// there would throw away which process acted.
    /// </summary>
    Guid? UserId => Actor?.UserId;
}
