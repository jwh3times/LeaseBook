using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// A renter (§C.1). The M1 journal carries this row's id as the <c>tenant_id</c> dimension (P26) and
/// now FKs to it (P38). The deposit-aggregate ids (<c>AggDepO1..8</c>, <c>AggregateDepositsUnattributed</c>)
/// and the statement-only ids (<c>TOkonkwo</c>, <c>TLiu</c>) are materialized as <see cref="IsSystem"/>
/// tenant rows purely to satisfy those FKs (§C.2) — they never appear in lists/search/CRUD.
/// </summary>
public sealed class Tenant : IOrgScoped
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public required string DisplayName { get; set; }

    public string? ContactEmail { get; set; }

    public string? ContactPhone { get; set; }

    public TenantStatus Status { get; set; }

    public bool IsSystem { get; set; }

    public DateTime CreatedAt { get; set; }
}
