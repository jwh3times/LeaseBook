using LeaseBook.SharedKernel;

namespace LeaseBook.Web.Tenancy;

/// <summary>
/// One immutable record of a write to an org-scoped entity. Org-scoped (RLS) and <b>append-only</b>:
/// its migration calls <c>RevokeAppendOnly</c> so even the runtime app role cannot UPDATE or DELETE
/// rows (CLAUDE.md trust-accounting invariant). Written by <see cref="Persistence.AppDbContext"/>'s
/// SaveChanges pass — one event per tracked change of an <see cref="IOrgScoped"/> entity. The first
/// real producers are M1's journal writes; in M0 the table exists and its isolation is proven.
/// </summary>
public sealed class AuditEvent : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    /// <summary>The acting user. Nullable until WP-06 (Identity) supplies it from the claim seam.</summary>
    public Guid? ActorUserId { get; set; }

    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }

    /// <summary><c>insert</c> | <c>update</c> | <c>delete</c>.</summary>
    public required string Action { get; set; }

    /// <summary>Original column values (jsonb); null for inserts.</summary>
    public string? Before { get; set; }

    /// <summary>New column values (jsonb); null for deletes.</summary>
    public string? After { get; set; }

    public DateTime OccurredAt { get; set; }
}
