using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Accounting.Persistence;

public sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> builder)
    {
        builder.ToTable("journal_entries", t =>
        {
            // ADR-039: attribution is durable, and the two cases are mutually exclusive — a user
            // entry names a user and no process, a system entry names a process and no user. The
            // null arm is legacy only: entries posted before ADR-039, where accountability cannot be
            // recovered and pretending otherwise would be worse than admitting it.
            t.HasCheckConstraint(
                "ck_journal_entries_actor_attribution",
                """
                actor_kind IS NULL
                OR (actor_kind = 'user' AND created_by IS NOT NULL AND actor_process IS NULL)
                OR (actor_kind = 'system' AND created_by IS NULL AND actor_process IS NOT NULL)
                """);
        });

        builder.HasKey(e => e.Id);
        builder.HasAlternateKey(e => new { e.OrgId, e.Id });
        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.EntryDate).IsRequired();
        builder.Property(e => e.DueDate);
        builder.Property(e => e.EventType).IsRequired();
        builder.Property(e => e.EventSubtype);
        builder.Property(e => e.Description);
        builder.Property(e => e.SourceRef);
        builder.Property(e => e.ReversesEntryId);
        builder.Property(e => e.AssessesEntryId);
        builder.Property(e => e.CreatedBy);
        builder.Property(e => e.ActorKind).HasMaxLength(8);
        builder.Property(e => e.ActorProcess).HasMaxLength(Actor.MaxProcessLength);
        builder.Property(e => e.PostedAt).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();

        // One header → many lines, mapped through the read-only Lines collection's backing field.
        builder.HasMany(e => e.Lines)
            .WithOne()
            .HasForeignKey(l => l.EntryId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Metadata.FindNavigation(nameof(JournalEntry.Lines))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // Self-reference: a reversal points at the entry it voids (no inverse navigation).
        builder.HasOne<JournalEntry>()
            .WithMany()
            .HasForeignKey(e => e.ReversesEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        // A late-fee assessment points at its specific rent obligation (no inverse navigation).
        builder.HasOne<JournalEntry>()
            .WithMany()
            .HasForeignKey(e => new { e.OrgId, e.AssessesEntryId })
            .HasPrincipalKey(e => new { e.OrgId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.OrgId, e.EntryDate });

        // Idempotency: at most one entry per (org, source_ref) when a source_ref is supplied.
        builder.HasIndex(e => new { e.OrgId, e.SourceRef })
            .IsUnique()
            .HasFilter("source_ref IS NOT NULL");

        // An entry is reversed at most once.
        builder.HasIndex(e => new { e.OrgId, e.ReversesEntryId })
            .IsUnique()
            .HasFilter("reverses_entry_id IS NOT NULL");

        // N.C. G.S. 42-46(b): at most one late fee may assess a specific rental payment.
        builder.HasIndex(e => new { e.OrgId, e.AssessesEntryId })
            .IsUnique()
            .HasFilter("assesses_entry_id IS NOT NULL");
    }
}
