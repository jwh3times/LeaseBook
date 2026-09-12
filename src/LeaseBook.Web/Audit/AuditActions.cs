namespace LeaseBook.Web.Audit;

/// <summary>
/// The <c>action</c> vocabulary of <c>audit_events</c> — what the review surface (#321) offers as its
/// event-kind filter. Three come from the auditing pass in <see cref="Persistence.AppDbContext"/>; the
/// rest are written by hand alongside a synthetic <c>entity_type</c>.
/// <para>
/// Enumerated rather than queried, for the same reason as <see cref="AuditEntityTypes"/>: a filter list
/// built from <c>SELECT DISTINCT</c> describes the rows that happen to exist instead of the events that
/// can occur. <c>AuditVocabularyDriftTests</c> scans source for these literals.
/// </para>
/// </summary>
public static class AuditActions
{
    /// <summary>Written by the tracked-change auditing pass, one per EF entity state.</summary>
    public static readonly IReadOnlyList<string> Tracked = ["insert", "update", "delete"];

    /// <summary>Written by hand: seeding, and the account-security lifecycle (ADR-043).</summary>
    public static readonly IReadOnlyList<string> Explicit =
    [
        "seed",
        "admin-created",
        "mfa-enrolled",
        "mfa-reset",
        "password-changed",
    ];

    /// <summary>Every action an audit row can carry, ordinal-sorted for a stable filter list.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        .. Tracked.Concat(Explicit).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal),
    ];
}
