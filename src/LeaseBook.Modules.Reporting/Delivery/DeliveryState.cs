namespace LeaseBook.Modules.Reporting.Delivery;

/// <summary>
/// Lifecycle state of a <see cref="StatementDeliveryRecord"/>.
/// Persisted as a snake_case text column via <see cref="DeliveryStateConverter"/>.
/// <para>
/// <list type="bullet">
/// <item><c>Queued</c> — artifact stored, email send pending (M8 ACS seam).</item>
/// <item><c>Sent</c> — email accepted by ACS (M8 transition).</item>
/// <item><c>Failed</c> — ACS reported a delivery failure (M8 transition).</item>
/// </list>
/// </para>
/// </summary>
public enum DeliveryState
{
    Queued,
    Sent,
    Failed,
}
