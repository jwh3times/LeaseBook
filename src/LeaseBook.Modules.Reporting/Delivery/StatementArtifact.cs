using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Reporting.Delivery;

/// <summary>
/// The immutable rendered statement for one owner, period and basis: the bytes an owner is entitled
/// to receive. Org-scoped (RLS) and genuinely append-only — the runtime role holds no
/// <c>UPDATE</c>/<c>DELETE</c> grant on this table.
/// <para>
/// An artifact is rendered once. Every send of it — including a retry after a bounce — is a separate
/// <see cref="StatementDeliveryAttempt"/> pointing back at this row, so two attempts for one period
/// cannot deliver two different PDFs (ADR-040). Re-rendering a period produces a new artifact, which
/// is a different document and says so.
/// </para>
/// <para>
/// The <see cref="ArtifactKey"/> is opaque — resolved to bytes via <c>IArtifactStore</c>. For the
/// local implementation it is a file name; for Blob/Azurite it is a container-relative path.
/// </para>
/// </summary>
public sealed class StatementArtifact : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    /// <summary>The owner the statement belongs to.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>Statement period — year.</summary>
    public int PeriodYear { get; set; }

    /// <summary>Statement period — month (1–12).</summary>
    public int PeriodMonth { get; set; }

    /// <summary>
    /// Accounting basis the statement was rendered on — <c>cash</c> or <c>accrual</c>. Part of the
    /// artifact's identity: the same owner and period on the other basis is a different document.
    /// <para>
    /// Null only on rows carried over from the pre-ADR-040 <c>statement_deliveries</c> table, which
    /// never recorded the basis. Deliberately not guessed during the backfill — the same posture
    /// ADR-039 took with pre-existing actor rows. Every artifact issued since sets it.
    /// </para>
    /// </summary>
    public string? Basis { get; set; }

    /// <summary>Opaque key into <c>IArtifactStore</c> for the immutable PDF bytes.</summary>
    public string ArtifactKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>Every send of this artifact, oldest first once loaded through the configured order.</summary>
    public List<StatementDeliveryAttempt> Attempts { get; } = [];
}
