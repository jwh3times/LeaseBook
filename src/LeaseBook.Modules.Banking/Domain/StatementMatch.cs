using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Banking.Domain;

/// <summary>
/// The recorded outcome of matching one statement line (P67, for audit): the register line it resolved to
/// (null when unmatched/created), the <see cref="Import.MatchKind"/> (<c>matched</c>/<c>suggested</c>/
/// <c>unmatched</c>/<c>created</c>), and who/when decided. <see cref="JournalLineId"/> is a plain reference
/// into Accounting's journal — no FK across the module boundary (ADR-007). Org-scoped (P70).
/// </summary>
public sealed class StatementMatch : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid StatementLineId { get; set; }

    public Guid? JournalLineId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public DateTime DecidedAt { get; set; }

    public Guid? DecidedBy { get; set; }
}
