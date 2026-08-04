using LeaseBook.Modules.Capabilities.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Capabilities.Persistence;

/// <summary>
/// Maps <see cref="PlatformAuditEvent"/> onto the hand-authored <c>platform_audit_events</c> table.
/// <c>org_id</c> is nullable and carries no FK on purpose (deleting an org must not delete the
/// record of what was done to it), so nothing here declares a relationship.
/// </summary>
public sealed class PlatformAuditEventConfiguration : IEntityTypeConfiguration<PlatformAuditEvent>
{
    public void Configure(EntityTypeBuilder<PlatformAuditEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("platform_audit_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.OccurredAt).IsRequired();
        builder.Property(e => e.Actor).IsRequired();
        builder.Property(e => e.Action).IsRequired();
        builder.Property(e => e.Capability);
        builder.Property(e => e.OrgId);
        builder.Property(e => e.DetailJson).IsRequired().HasColumnType("jsonb");
    }
}
