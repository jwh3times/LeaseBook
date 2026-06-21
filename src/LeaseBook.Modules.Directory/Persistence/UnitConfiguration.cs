using LeaseBook.Modules.Directory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Directory.Persistence;

public sealed class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.ToTable("units", t =>
            t.HasCheckConstraint("ck_units_status", $"status IN ({DirectorySql.Quote(UnitStatusConverter.DbValues)})"));

        builder.HasKey(e => e.Id);

        // (org_id, id) alternate key — target of journal_lines' composite dimension FK (ADR-013, P60).
        builder.HasAlternateKey(e => new { e.OrgId, e.Id });

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.PropertyId).IsRequired();
        builder.Property(e => e.Label).IsRequired();
        builder.Property(e => e.Rent).IsRequired().HasDefaultValueSql("0");
        builder.Property(e => e.Status).IsRequired().HasConversion<UnitStatusConverter>();
        builder.Property(e => e.IsSystem).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.CreatedAt).IsRequired();

        builder.HasOne<Property>().WithMany().HasForeignKey(e => e.PropertyId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.OrgId, e.PropertyId });
        // GIN trigram index on label added in the AddDirectory migration (raw SQL).
    }
}
