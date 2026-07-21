namespace LeaseBook.Web.Observability;

/// <summary>
/// Stable <see cref="EventId"/>s for structured logs. Track B's B4 alert rules key on these —
/// never renumber an existing id; add new ones at the end of the range.
/// 1000-1099 = host/error plumbing. 1100+ reserved for domain areas.
/// </summary>
public static class LogEvents
{
    public static readonly EventId UnhandledException = new(1000, nameof(UnhandledException));
    public static readonly EventId DomainRejection = new(1001, nameof(DomainRejection));
    public static readonly EventId ValidationRejection = new(1002, nameof(ValidationRejection));
    public static readonly EventId ImportRowFailed = new(1003, nameof(ImportRowFailed));

    // 1100-1199 = import correction / supersede (WP-7). First domain-area block per the 1100+ rule.
    public static readonly EventId SupersedeReversalRace = new(1100, nameof(SupersedeReversalRace));
}
