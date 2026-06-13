using LeaseBook.Modules.Accounting.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Accounting.Persistence;

public sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> builder)
    {
        builder.ToTable("journal_entries");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.EntryDate).IsRequired();
        builder.Property(e => e.EventType).IsRequired();
        builder.Property(e => e.EventSubtype);
        builder.Property(e => e.Description);
        builder.Property(e => e.SourceRef);
        builder.Property(e => e.ReversesEntryId);
        builder.Property(e => e.CreatedBy);
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

        builder.HasIndex(e => new { e.OrgId, e.EntryDate });

        // Idempotency: at most one entry per (org, source_ref) when a source_ref is supplied.
        builder.HasIndex(e => new { e.OrgId, e.SourceRef })
            .IsUnique()
            .HasFilter("source_ref IS NOT NULL");

        // An entry is reversed at most once.
        builder.HasIndex(e => new { e.OrgId, e.ReversesEntryId })
            .IsUnique()
            .HasFilter("reverses_entry_id IS NOT NULL");
    }
}
