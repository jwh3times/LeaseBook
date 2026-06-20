using LeaseBook.Modules.Directory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Directory.Persistence;

public sealed class OwnerConfiguration : IEntityTypeConfiguration<Owner>
{
    public void Configure(EntityTypeBuilder<Owner> builder)
    {
        builder.ToTable("owners");
        builder.HasKey(e => e.Id);

        // (org_id, id) alternate key — target of journal_lines' composite dimension FK (ADR-013, P60),
        // so the constraint enforces org-correctness, not just existence. id alone is already the PK.
        builder.HasAlternateKey(e => new { e.OrgId, e.Id });

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.Name).IsRequired();
        builder.Property(e => e.Initials);
        builder.Property(e => e.ContactEmail);
        builder.Property(e => e.ContactPhone);
        builder.Property(e => e.DefaultMgmtFeeBps);

        // Money convention → NUMERIC(14,2). reserve_amount NOT NULL DEFAULT 0 (§C.1).
        builder.Property(e => e.ReserveAmount).IsRequired().HasDefaultValueSql("0");
        builder.Property(e => e.IsSystem).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.CreatedAt).IsRequired();

        // Org-leading. The GIN trigram index on name is added in the AddDirectory migration (raw SQL,
        // not modeled — pg_trgm operator classes are not expressible through the EF index model).
        builder.HasIndex(e => new { e.OrgId, e.Name });
    }
}
