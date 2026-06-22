using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Reporting.Delivery;

/// <summary>
/// One statement delivery attempt: the PDF artifact key, the recipient address, and the
/// current state. Org-scoped (RLS) and append-only — corrections are new rows, never updates.
/// <para>
/// The <c>artifact_key</c> is opaque — resolved to bytes via <c>IArtifactStore</c>. For the
/// local implementation it is a file name; for Blob/Azurite it is a container-relative path.
/// </para>
/// <para>
/// Inserted by <c>LocalStatementDelivery</c> with state <see cref="DeliveryState.Queued"/>.
/// Transitions to <see cref="DeliveryState.Sent"/> or <see cref="DeliveryState.Failed"/> when the
/// M8 ACS email send completes — that transition is <b>not implemented here</b>.
/// </para>
/// </summary>
public sealed class StatementDeliveryRecord : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    /// <summary>The owner whose statement was delivered.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>Statement period — year.</summary>
    public int PeriodYear { get; set; }

    /// <summary>Statement period — month (1–12).</summary>
    public int PeriodMonth { get; set; }

    /// <summary>Recipient email address.</summary>
    public string ToEmail { get; set; } = string.Empty;

    /// <summary>Current delivery state (Queued → Sent | Failed via the M8 ACS seam).</summary>
    public DeliveryState State { get; set; }

    /// <summary>Opaque key into <c>IArtifactStore</c> for the immutable PDF bytes.</summary>
    public string ArtifactKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
