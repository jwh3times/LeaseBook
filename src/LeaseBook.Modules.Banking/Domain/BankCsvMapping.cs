using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Banking.Domain;

/// <summary>
/// A saved per-bank CSV column mapping (P66): the named layout an operator picks instead of re-mapping
/// columns on every import. <see cref="ColumnMapJson"/> holds the serialized
/// <see cref="Import.ColumnMap"/> (jsonb). Org-scoped (P70).
/// </summary>
public sealed class BankCsvMapping : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid BankAccountId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ColumnMapJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
