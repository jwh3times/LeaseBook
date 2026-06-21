using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Accounting.Domain;

/// <summary>
/// The mutable clearance state of one bank journal line (M4 / ADR-014, P62). Keyed 1:1 to the
/// <c>journal_lines.id</c> it annotates; absence of a row ≡ <see cref="BankLineStatus.Uncleared"/>. This
/// is operational metadata, <b>not</b> a journal row, so the runtime role keeps INSERT/UPDATE on it
/// (the journal stays append-only and byte-stable). Written through raw upserts by the clearance command
/// (WP-03) and reconciliation finalize (WP-04); the register reads it through a LEFT JOIN.
/// </summary>
public sealed class BankLineState : IOrgScoped
{
    /// <summary>PK and FK to <c>journal_lines.id</c> (single-column — the journal PK is globally unique, P61).</summary>
    public Guid JournalLineId { get; set; }

    public Guid OrgId { get; set; }

    public BankLineStatus Status { get; set; }

    public DateTime? ClearedAt { get; set; }

    /// <summary>The finalized reconciliation that locked this line, once reconciled (WP-04). No FK until B.2 exists.</summary>
    public Guid? ReconciliationId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
