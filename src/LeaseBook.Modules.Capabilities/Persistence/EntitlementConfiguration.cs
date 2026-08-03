using LeaseBook.Modules.Capabilities.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Capabilities.Persistence;

/// <summary>
/// Maps <see cref="Entitlement"/> onto the hand-authored <c>entitlements</c> table
/// (M8_AddPlatformCapabilities). The DDL — including RLS, the append-only REVOKE and the FK to
/// <c>orgs</c> — is owned by that migration, not by this configuration; see
/// <c>M8_ReconcileCapabilitiesModelSnapshot</c> for why the model is reconciled to it rather than
/// the other way round.
/// <para>
/// No query filter and no <c>IOrgScoped</c>: the platform plane must be able to read across orgs,
/// which the convention-driven org filter in AppDbContext would silently prevent.
/// </para>
/// </summary>
public sealed class EntitlementConfiguration : IEntityTypeConfiguration<Entitlement>
{
    public void Configure(EntityTypeBuilder<Entitlement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("entitlements");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.Capability).IsRequired();
        builder.Property(e => e.Granted).IsRequired();
        builder.Property(e => e.EffectiveAt).IsRequired();
        builder.Property(e => e.Actor).IsRequired();

        // Serves the resolver's "latest row per (org, capability)" read.
        builder.HasIndex(e => new { e.OrgId, e.Capability, e.EffectiveAt });
    }
}
