using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Banking.Domain;

/// <summary>
/// One CSV import run for a bank account (ADR-015): the uploaded file's name, who/when, the number of
/// lines actually stored (after dedup), and a status. Its <see cref="StatementLine"/>s reference it. The
/// <c>imported_by</c> actor comes from <c>IActorContext</c>. Org-scoped (P70).
/// </summary>
public sealed class StatementImport : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid BankAccountId { get; set; }

    public string Filename { get; set; } = string.Empty;

    public DateTime ImportedAt { get; set; }

    public Guid? ImportedBy { get; set; }

    /// <summary>The number of new statement lines stored (duplicates skipped are not counted).</summary>
    public int RowCount { get; set; }

    public string Status { get; set; } = "completed";
}
