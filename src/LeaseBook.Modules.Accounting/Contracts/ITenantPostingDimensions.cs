namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// Consumer-owned read port (ADR-007 / P49 / P58) for the posting dimensions a tenant write needs.
/// The M3 composer sends only a tenant id + amount/bank (never owner/property/unit, M3-E3); the
/// Accounting command resolves the tenant's <c>(ownerId, propertyId, unitId)</c> from the <b>active
/// lease</b> through this port. Accounting depends only on this abstraction; the <b>host</b> adapter
/// delegates to a Directory query via <c>ISender</c>, so the cross-module reference lives in the host,
/// never in Accounting (which never references Directory's entity types or tables, M3-E12).
/// <para>
/// This is a <b>single-entity write read</b> (one tenant per post), so the "batch maps only" rule
/// (M2-E12) does not apply — that rule governs list/roster N+1, not a one-tenant post. A tenant with no
/// active lease returns <see langword="null"/>, and the command rejects (never a silent default, P58).
/// </para>
/// </summary>
public interface ITenantPostingDimensions
{
    Task<TenantPostingDimensions?> GetAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>The owner/property/unit a tenant's postings carry, resolved from the active lease (P58).</summary>
public sealed record TenantPostingDimensions(Guid OwnerId, Guid PropertyId, Guid? UnitId);
