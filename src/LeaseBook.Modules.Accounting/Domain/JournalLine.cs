using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Accounting.Domain;

/// <summary>
/// One side of a journal entry (§C.1). Exactly one of <see cref="Debit"/>/<see cref="Credit"/> is
/// non-null and positive (DB CHECK + P25). <see cref="AccountClass"/> is <b>denormalized</b> from the
/// referenced account by the posting service (never trusted from the caller) so DB CHECKs — including
/// the PM-income/owner-dim exclusion — can reason about it without a join. Dimensions
/// (<see cref="PropertyId"/> … <see cref="BankAccountId"/>) are bare uuids in M1 (P26): the directory
/// tables they will FK to do not exist until M2/M4. Append-only — never updated or deleted.
/// </summary>
public sealed class JournalLine : IOrgScoped
{
    private JournalLine()
    {
        // EF + the factory below.
    }

    private JournalLine(
        Guid accountId,
        AccountClass accountClass,
        Money? debit,
        Money? credit,
        EntryBasis basis,
        Guid? propertyId,
        Guid? unitId,
        Guid? ownerId,
        Guid? tenantId,
        Guid? bankAccountId,
        string? memo)
    {
        Id = UuidV7.NewId();
        AccountId = accountId;
        AccountClass = accountClass;
        Debit = debit;
        Credit = credit;
        Basis = basis;
        PropertyId = propertyId;
        UnitId = unitId;
        OwnerId = ownerId;
        TenantId = tenantId;
        BankAccountId = bankAccountId;
        Memo = memo;
    }

    public Guid Id { get; private set; }

    public Guid OrgId { get; set; }

    /// <summary>Owning entry. Set by EF through the entry's <c>Lines</c> relationship on save.</summary>
    public Guid EntryId { get; private set; }

    public Guid AccountId { get; private set; }

    public AccountClass AccountClass { get; private set; }

    public Money? Debit { get; private set; }

    public Money? Credit { get; private set; }

    public EntryBasis Basis { get; private set; }

    public Guid? PropertyId { get; private set; }

    public Guid? UnitId { get; private set; }

    public Guid? OwnerId { get; private set; }

    public Guid? TenantId { get; private set; }

    public Guid? BankAccountId { get; private set; }

    public string? Memo { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>Module-internal factory — only the posting service (WP-04) constructs lines.</summary>
    internal static JournalLine Create(
        Guid accountId,
        AccountClass accountClass,
        Money? debit,
        Money? credit,
        EntryBasis basis,
        Guid? propertyId = null,
        Guid? unitId = null,
        Guid? ownerId = null,
        Guid? tenantId = null,
        Guid? bankAccountId = null,
        string? memo = null) =>
        new(accountId, accountClass, debit, credit, basis,
            propertyId, unitId, ownerId, tenantId, bankAccountId, memo);
}
