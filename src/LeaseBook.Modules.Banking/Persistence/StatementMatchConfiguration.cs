using LeaseBook.Modules.Banking.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Banking.Persistence;

public sealed class StatementMatchConfiguration : IEntityTypeConfiguration<StatementMatch>
{
    public void Configure(EntityTypeBuilder<StatementMatch> builder)
    {
        builder.ToTable("statement_matches", t =>
            t.HasCheckConstraint("ck_statement_matches_kind",
                "kind IN ('matched','suggested','unmatched','created')"));

        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.StatementLineId).IsRequired();
        builder.Property(e => e.JournalLineId);
        builder.Property(e => e.Kind).IsRequired();
        builder.Property(e => e.DecidedAt).IsRequired();
        builder.Property(e => e.DecidedBy);

        // Same-module FK to the statement line; journal_line_id stays a bare reference (no cross-module FK, ADR-007).
        builder.HasOne<StatementLine>()
            .WithMany()
            .HasForeignKey(e => e.StatementLineId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.StatementLineId);
    }
}
