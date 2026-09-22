using LeaseBook.Modules.Directory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Directory.Persistence;

public sealed class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.ToTable("units", t =>
            t.HasCheckConstraint(
                "ck_units_availability",
                $"availability IN ({DirectorySql.Quote(UnitAvailabilityConverter.DbValues)})"));

        builder.HasKey(e => e.Id);

        // (org_id, id) alternate key — target of journal_lines' composite dimension FK (ADR-013, P60).
        builder.HasAlternateKey(e => new { e.OrgId, e.Id }).HasName("ak_units_org_id_id");

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.PropertyId).IsRequired();
        builder.Property(e => e.Label).IsRequired();
        builder.Property(e => e.Rent).IsRequired().HasDefaultValueSql("0");
        builder.Property(e => e.Availability).IsRequired().HasConversion<UnitAvailabilityConverter>();
        builder.Property(e => e.IsSystem).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.CreatedAt).IsRequired();

        // Composite on (org_id, property_id): FK checks bypass RLS, so a single-column key would only
        // prove the property exists in SOME organization, not in this unit's own.
        builder.HasOne<Property>().WithMany()
            .HasForeignKey(e => new { e.OrgId, e.PropertyId })
            .HasPrincipalKey(p => new { p.OrgId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.OrgId, e.PropertyId });
        // GIN trigram index on label added in the AddDirectory migration (raw SQL).
    }
}
