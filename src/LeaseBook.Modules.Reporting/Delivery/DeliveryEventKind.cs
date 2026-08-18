namespace LeaseBook.Modules.Reporting.Delivery;

/// <summary>
/// What a <see cref="StatementDeliveryEvent"/> records about one
/// <see cref="StatementDeliveryAttempt"/>. Persisted as a snake_case text column via
/// <see cref="DeliveryEventKindConverter"/>.
/// <para>
/// Events are facts, not a stored state machine. The attempt's current delivery status is the kind
/// of its latest recorded event (ADR-040) — no row anywhere holds a status column, so a later fact
/// never erases an earlier one.
/// </para>
/// <para>
/// <list type="bullet">
/// <item>
/// <c>Queued</c> — the artifact is stored and the attempt exists; the send has not been made yet.
/// Recorded once, with the attempt itself.
/// </item>
/// <item>
/// <c>Accepted</c> — the email provider accepted the message for delivery. This is an acceptance by
/// the provider and <b>not</b> evidence the owner received anything. Deliberately not called
/// <c>Sent</c>: the pre-ADR-040 model used that name for exactly this fact and read as success.
/// </item>
/// <item><c>Delivered</c> — the recipient's mail server accepted the message.</item>
/// <item>
/// <c>Bounced</c> — the recipient's mail server rejected the message after provider acceptance.
/// </item>
/// <item>
/// <c>Failed</c> — the message never reached the provider, or the provider reported a terminal
/// failure before accepting it.
/// </item>
/// </list>
/// </para>
/// </summary>
public enum DeliveryEventKind
{
    Queued,
    Accepted,
    Delivered,
    Bounced,
    Failed,
}
