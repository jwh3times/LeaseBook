using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Reporting.Delivery;

/// <summary>
/// One request to send a <see cref="StatementArtifact"/> to one destination. Org-scoped (RLS) and
/// genuinely append-only — the runtime role holds no <c>UPDATE</c>/<c>DELETE</c> grant on this table.
/// <para>
/// An attempt carries no status column. What happened to it is the ordered
/// <see cref="StatementDeliveryEvent"/> history hanging off it, and its current delivery status is
/// the kind of the latest recorded event (ADR-040). A retry after a bounce is a <b>new attempt</b>
/// against the same artifact, never a new event on this one — which is what keeps a retry
/// distinguishable from a status change.
/// </para>
/// </summary>
public sealed class StatementDeliveryAttempt : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    /// <summary>The artifact being sent. Composite <c>(org_id, artifact_id)</c> FK.</summary>
    public Guid ArtifactId { get; set; }

    public StatementArtifact? Artifact { get; set; }

    /// <summary>
    /// Recipient email address. PII — never logged, and read only through this org-scoped row.
    /// </summary>
    public string ToEmail { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// This attempt's event history, oldest first. Pass it to <see cref="DeliveryStatus.Of(StatementDeliveryAttempt)"/>
    /// rather than reading the last element — see <see cref="StatementDeliveryEvent.Sequence"/> for
    /// why recorded order rather than provider order decides the current status.
    /// </summary>
    public List<StatementDeliveryEvent> Events { get; } = [];
}
