using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// A real-world bank account the PM holds (trust, deposit or operating; §C.1). Creating one provisions
/// the matching chart-of-accounts account in Accounting through the WP-02 cross-module port (P49). The
/// journal's <c>bank_account_id</c> dimension FKs here (P38). No delete in M2 — an account with journal
/// history cannot be removed; <see cref="IsActive"/> deactivation lands in M4.
/// </summary>
public sealed class BankAccount : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public required string Name { get; set; }

    public string? Institution { get; set; }

    /// <summary>Last-4 account mask, e.g. "4021".</summary>
    public string? Mask { get; set; }

    public BankPurpose Purpose { get; set; }

    /// <summary>M4 deactivation flag. M2 only ever creates active accounts.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}
