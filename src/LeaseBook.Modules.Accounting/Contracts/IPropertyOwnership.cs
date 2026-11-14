namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// Consumer-owned read port (ADR-007) for the owner of one property on one date: the owner an
/// owner-attributed posting must name (#744). Ownership is effective-dated, so the answer is the owner
/// after every transfer effective on or before the date. The <b>host</b> adapter delegates to a
/// Directory query via <c>ISender</c> on the ambient org transaction, which holds a shared lock on the
/// property row until the posting commits, so a concurrent ownership transfer lands wholly before or
/// after it. A property the caller's org cannot see, or a system property, answers
/// <see langword="null"/>.
/// <para>
/// Like <see cref="ITenantPostingDimensions"/> this is a <b>single-entity write read</b> (one property
/// per post), so the batch-maps rule for list reads does not apply.
/// </para>
/// </summary>
public interface IPropertyOwnership
{
    Task<Guid?> GetOwnerOnAsync(Guid propertyId, DateOnly date, CancellationToken ct);
}
