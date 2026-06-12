using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Web.Tenancy;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        // snake_case convention maps AuditEvent->audit_events, ActorUserId->actor_user_id, etc.
        builder.ToTable("audit_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.EntityType).IsRequired().HasMaxLength(200);
        builder.Property(e => e.EntityId).IsRequired();
        builder.Property(e => e.Action).IsRequired().HasMaxLength(16);
        builder.Property(e => e.Before).HasColumnType("jsonb");
        builder.Property(e => e.After).HasColumnType("jsonb");
        builder.Property(e => e.OccurredAt).IsRequired();

        // Composite indexes lead with org_id so the RLS equality predicate rides every access path (§1).
        builder.HasIndex(e => new { e.OrgId, e.OccurredAt });
        builder.HasIndex(e => new { e.OrgId, e.EntityType, e.EntityId });
    }
}
