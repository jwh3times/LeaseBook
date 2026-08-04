using LeaseBook.Modules.Capabilities.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Capabilities.Persistence;

/// <summary>
/// Maps <see cref="CapabilityCohort"/> onto the hand-authored <c>capability_cohorts</c> table. No
/// query filter and no <c>IOrgScoped</c> — see <see cref="EntitlementConfiguration"/>.
/// </summary>
public sealed class CapabilityCohortConfiguration : IEntityTypeConfiguration<CapabilityCohort>
{
    public void Configure(EntityTypeBuilder<CapabilityCohort> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("capability_cohorts");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Capability).IsRequired();
        builder.Property(c => c.OrgId).IsRequired();
        builder.Property(c => c.UserId);
        builder.Property(c => c.AddedAt).IsRequired();
        builder.Property(c => c.AddedBy).IsRequired();

        // Serves the resolver's "is this org (or this user in it) in the cohort" read.
        builder.HasIndex(c => new { c.Capability, c.OrgId });
    }
}
