using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Reporting.Delivery;

/// <summary>
/// One recorded fact about a <see cref="StatementDeliveryAttempt"/>: it was queued, the provider
/// accepted it, the recipient's server took it, it bounced, or it failed. Org-scoped (RLS) and
/// genuinely append-only — the runtime role holds no <c>UPDATE</c>/<c>DELETE</c> grant on this table.
/// <para>
/// Nothing is ever corrected in place. An acceptance followed by a bounce is two rows, and both
/// survive; the pre-ADR-040 model overwrote the first with the second and lost the acceptance
/// entirely.
/// </para>
/// </summary>
public sealed class StatementDeliveryEvent : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    /// <summary>The attempt this fact is about. Composite <c>(org_id, attempt_id)</c> FK.</summary>
    public Guid AttemptId { get; set; }

    public StatementDeliveryAttempt? Attempt { get; set; }

    /// <summary>
    /// Position of this event in its attempt's history, 1-based. The <c>Queued</c> event is always 1.
    /// <para>
    /// This is what orders the history, and a unique index on <c>(org_id, attempt_id, sequence)</c>
    /// enforces it. Recorded order is stored rather than inferred: ordering on <see cref="Id"/> would
    /// depend on UUID-v7 bytes sorting the same way in Postgres and in .NET's <c>Guid</c> comparer
    /// (they do not), and ordering on <see cref="OccurredAt"/> would let a late provider callback
    /// carrying an early timestamp overwrite a status already learned.
    /// </para>
    /// </summary>
    public int Sequence { get; set; }

    /// <summary>What happened.</summary>
    public DeliveryEventKind Kind { get; set; }

    /// <summary>
    /// When the fact happened according to whoever reported it — the provider's own timestamp for a
    /// provider event.
    /// <para>
    /// <b>This does not order the history.</b> Provider webhooks arrive late and out of order, and two
    /// events can share a timestamp, so ordering on this column would let a stale callback silently
    /// become the current status. <see cref="Sequence"/> decides; this column is evidence, not
    /// sequence (ADR-040).
    /// </para>
    /// </summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// The email provider's own identifier for the message, when it gave one — the handle an operator
    /// needs to look the send up on the provider's side. Null for <c>Queued</c>, which precedes any
    /// provider contact.
    /// </summary>
    public string? ProviderMessageId { get; set; }

    /// <summary>
    /// Short operator-facing detail: a bounce reason or failure code. Must not carry the recipient
    /// address or any other PII — the address lives on the attempt, behind org scoping.
    /// </summary>
    public string? Detail { get; set; }

    public DateTime CreatedAt { get; set; }
}
