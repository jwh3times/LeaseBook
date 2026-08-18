using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Web.Tenancy;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        // snake_case convention maps AuditEvent->audit_events, ActorUserId->actor_user_id, etc.
        builder.ToTable("audit_events", t =>
        {
            // ADR-039, same rule as journal_entries: exactly one of the two attributions, or the
            // legacy null for rows written before the invariant existed.
            t.HasCheckConstraint(
                "ck_audit_events_actor_attribution",
                """
                actor_kind IS NULL
                OR (actor_kind = 'user' AND actor_user_id IS NOT NULL AND actor_process IS NULL)
                OR (actor_kind = 'system' AND actor_user_id IS NULL AND actor_process IS NOT NULL)
                """);
        });
        builder.HasKey(e => e.Id);
        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.EntityType).IsRequired().HasMaxLength(200);
        builder.Property(e => e.EntityId).IsRequired();
        builder.Property(e => e.Action).IsRequired().HasMaxLength(16);
        builder.Property(e => e.ActorKind).HasMaxLength(8);
        builder.Property(e => e.ActorProcess).HasMaxLength(Actor.MaxProcessLength);
        builder.Property(e => e.Before).HasColumnType("jsonb");
        builder.Property(e => e.After).HasColumnType("jsonb");
        builder.Property(e => e.OccurredAt).IsRequired();

        // Composite indexes lead with org_id so the RLS equality predicate rides every access path (§1).
        builder.HasIndex(e => new { e.OrgId, e.OccurredAt });
        builder.HasIndex(e => new { e.OrgId, e.EntityType, e.EntityId });
    }
}
