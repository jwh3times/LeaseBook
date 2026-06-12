using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Accounting.Domain;

/// <summary>
/// A chart-of-accounts row: one per org, keyed by stable <see cref="Code"/> (§C.1/§C.2). Singleton
/// accounts (receivable, owner equity, the two deposit-liability accounts, PM income) carry no
/// <see cref="BankAccountId"/>; the per-bank accounts (<see cref="AccountClass.TrustBank"/> /
/// <see cref="AccountClass.PmOperatingBank"/>) carry the id of the bank they represent — a DB CHECK
/// enforces that correspondence. Provisioned by WP-03 inside the ambient org scope.
/// </summary>
public sealed class Account : IOrgScoped
{
    private Account()
    {
        // EF materialization + the parameter-bearing factory below are the only constructors.
        Code = null!;
        Name = null!;
    }

    private Account(string code, AccountClass @class, string name, Guid? bankAccountId)
    {
        Id = UuidV7.NewId();
        Code = code;
        Class = @class;
        Name = name;
        BankAccountId = bankAccountId;
    }

    public Guid Id { get; private set; }

    /// <summary>Set by the org-stamping interceptor on insert (§C.4); never assigned by callers.</summary>
    public Guid OrgId { get; set; }

    /// <summary>Stable provisioning key, unique per org. Templates resolve accounts by code, never by name.</summary>
    public string Code { get; private set; }

    public AccountClass Class { get; private set; }

    public string Name { get; private set; }

    /// <summary>The bank this account represents; non-null iff <see cref="Class"/> is a bank class (DB CHECK).</summary>
    public Guid? BankAccountId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>Module-internal factory — the chart-of-accounts provisioner (WP-03) is the only caller.</summary>
    internal static Account Create(string code, AccountClass @class, string name, Guid? bankAccountId) =>
        new(code, @class, name, bankAccountId);
}
