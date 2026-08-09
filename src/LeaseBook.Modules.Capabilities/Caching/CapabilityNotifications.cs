namespace LeaseBook.Modules.Capabilities.Caching;

/// <summary>
/// The Postgres <c>LISTEN</c>/<c>NOTIFY</c> channel that carries capability invalidation (ADR-028).
/// <para>
/// It lives here because this module owns the thing a notification acts on —
/// <see cref="CapabilityCache.Invalidate"/> — and a module that cannot name the channel which wakes
/// it does not really own its own propagation. Previously the constant sat on the host's listener,
/// so the writer had to reach sideways into an unrelated host adapter to publish on it.
/// </para>
/// <para>
/// Both halves must agree or propagation silently degrades to the 30-second TTL: correct, slower,
/// and with no symptom beyond <see cref="CapabilityCache.NotificationRefreshes"/> staying at zero.
/// One constant, referenced by both, is what makes disagreement impossible rather than unlikely.
/// </para>
/// </summary>
public static class CapabilityNotifications
{
    /// <summary>The channel name. Shared by the listener and the platform writer — do not fork it.</summary>
    public const string Channel = "leasebook_capabilities";
}
