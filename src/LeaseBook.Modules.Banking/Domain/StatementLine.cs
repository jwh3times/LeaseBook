using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Banking.Domain;

/// <summary>
/// One parsed line from an imported statement (ADR-015). <see cref="Amount"/> is signed (deposit +,
/// withdrawal −). <see cref="DedupHash"/> + the <c>UNIQUE (org_id, bank_account_id, dedup_hash)</c> make
/// re-imports idempotent — a colliding line is skipped, never stored twice. <see cref="BankAccountId"/> is
/// carried on the row (denormalized from the import) so dedup spans <i>all</i> imports for that account,
/// not just one file. Org-scoped (P70).
/// </summary>
public sealed class StatementLine : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid BankAccountId { get; set; }

    public Guid ImportId { get; set; }

    public DateOnly StatementDate { get; set; }

    public string Description { get; set; } = string.Empty;

    public Money Amount { get; set; }

    public string DedupHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
