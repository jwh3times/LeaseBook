using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Capabilities.Domain;

/// <summary>
/// The append-only record of what the platform plane did: who granted what to whom, who toggled
/// which flag. The organization audit trail structurally cannot hold these rows — AppDbContext's audit
/// pass filters to IOrgScoped and throws without organization context, <c>AuditEvent.OrgId</c> is
/// non-nullable, and <c>audit_events</c> is RLS'd under FORCE with no platform escape.
/// <para>
/// <see cref="OrgId"/> is NULLABLE (creating a flag is not org-specific) and is deliberately NOT a
/// foreign key: deleting an org must not delete the record of what was done to it. Consequently
/// this type is <b>not IOrgScoped</b> either — see <see cref="Entitlement"/>.
/// </para>
/// </summary>
public sealed class PlatformAuditEvent
{
    /// <summary>App-generated UUIDv7 (P6) — never a database default.</summary>
    public Guid Id { get; set; } = UuidV7.NewId();

    public DateTime OccurredAt { get; set; }

    /// <summary>The platform operator identity that performed the action.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>Dotted action name, e.g. <c>entitlement.grant</c> or <c>flag.toggle</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Capability the action targeted; null for actions that name no capability.</summary>
    public string? Capability { get; set; }

    /// <summary>Org the action targeted; null for deployment-wide actions such as a flag toggle.</summary>
    public Guid? OrgId { get; set; }

    /// <summary>Action-specific payload, stored as <c>jsonb</c>.</summary>
    public string DetailJson { get; set; } = "{}";
}
