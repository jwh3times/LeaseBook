namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// Reserved <c>event_type</c> values that are not part of the postable business-event catalog (WP-05).
/// <see cref="EntryVoided"/> is owned by the reversal service (P27) — it is never posted directly.
/// </summary>
public static class EventTypes
{
    public const string EntryVoided = "EntryVoided";
}
