using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Capabilities.Domain;

/// <summary>
/// One append-only grant EVENT. Current state is the latest row per (OrgId, Capability) ordered by
/// EffectiveAt. There is deliberately no RevokedAt column: mutating a grant would require UPDATE on
/// the table, which would defeat RevokeAppendOnly and leave re-grant-after-revoke undefined.
/// <para>
/// <b>Not IOrgScoped, and this is load-bearing.</b> This is platform data ABOUT an org, not organization
/// data belonging to one. AppDbContext applies the org global query filter BY CONVENTION to every
/// IOrgScoped entity, so adding that interface here would silently empty every cross-org platform
/// read that RLS deliberately permits — with no test failing, because the write side throws loudly
/// while the read side just returns nothing. Isolation comes from RLS plus the single platform
/// escape (ADR-028), not from the EF tenancy pass. PlatformEntityModelGuardTests enforces this.
/// </para>
/// </summary>
public sealed class Entitlement
{
    /// <summary>
    /// App-generated UUIDv7 (P6), like every other key in the system — never a database default.
    /// Note it is <b>not</b> a usable tie-break for two grants at the same instant:
    /// <c>Guid.CreateVersion7</c> carries a millisecond timestamp with random low bits and no
    /// monotonic counter, so ids minted in the same millisecond sort arbitrarily. The unique index
    /// <c>ux_entitlements_org_capability_effective_at</c> is what makes that tie impossible.
    /// </summary>
    public Guid Id { get; set; } = UuidV7.NewId();

    public Guid OrgId { get; set; }

    public string Capability { get; set; } = string.Empty;

    public bool Granted { get; set; }

    public DateTime EffectiveAt { get; set; }

    /// <summary>Who performed the grant — a platform operator identity, not an org user.</summary>
    public string Actor { get; set; } = string.Empty;
}
