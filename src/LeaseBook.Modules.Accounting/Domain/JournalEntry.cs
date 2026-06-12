using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Accounting.Domain;

/// <summary>
/// A double-entry journal header plus its lines (§C.1). Written <b>only</b> through the posting
/// service (WP-04) — the single write path — so every entry is balanced per basis before it persists.
/// Append-only: a correction is a linked reversal entry (<see cref="ReversesEntryId"/>), never an
/// update or delete of a posted row (CLAUDE.md). <see cref="EntryDate"/> determines the accounting
/// period; <see cref="PostedAt"/> is the wall-clock posting time used for stable ledger ordering.
/// </summary>
public sealed class JournalEntry : IOrgScoped
{
    private readonly List<JournalLine> _lines = [];

    private JournalEntry()
    {
        // EF + the factory below.
        EventType = null!;
    }

    private JournalEntry(
        DateOnly entryDate,
        string eventType,
        string? eventSubtype,
        string? description,
        string? sourceRef,
        Guid? reversesEntryId,
        Guid? createdBy,
        DateTime postedAt)
    {
        Id = UuidV7.NewId();
        EntryDate = entryDate;
        EventType = eventType;
        EventSubtype = eventSubtype;
        Description = description;
        SourceRef = sourceRef;
        ReversesEntryId = reversesEntryId;
        CreatedBy = createdBy;
        PostedAt = postedAt;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; set; }

    /// <summary>Accounting date; the period (open/closed) derives from it.</summary>
    public DateOnly EntryDate { get; private set; }

    /// <summary>Business-event catalog name, e.g. <c>RentCharged</c> (§C.3).</summary>
    public string EventType { get; private set; }

    /// <summary>Fee kind or payment method (§C.1); null when the event has no subtype.</summary>
    public string? EventSubtype { get; private set; }

    public string? Description { get; private set; }

    /// <summary>Idempotency / source-document key; unique per org when present.</summary>
    public string? SourceRef { get; private set; }

    /// <summary>The entry this one reverses (void), if any; an entry can be reversed at most once.</summary>
    public Guid? ReversesEntryId { get; private set; }

    /// <summary>Acting user id; null for the seeder and background jobs.</summary>
    public Guid? CreatedBy { get; private set; }

    public DateTime PostedAt { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>The entry's lines, in the order they were added. Exposed read-only; mutated via <see cref="AddLine"/>.</summary>
    public IReadOnlyList<JournalLine> Lines => _lines;

    /// <summary>Module-internal factory — the posting service (WP-04) is the only caller.</summary>
    internal static JournalEntry Create(
        DateOnly entryDate,
        string eventType,
        string? eventSubtype,
        string? description,
        string? sourceRef,
        Guid? reversesEntryId,
        Guid? createdBy,
        DateTime postedAt) =>
        new(entryDate, eventType, eventSubtype, description, sourceRef, reversesEntryId, createdBy, postedAt);

    internal void AddLine(JournalLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _lines.Add(line);
    }
}
