using LeaseBook.Modules.Directory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Directory.Persistence;

public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants", t =>
            t.HasCheckConstraint("ck_tenants_status", $"status IN ({DirectorySql.Quote(TenantStatusConverter.DbValues)})"));

        builder.HasKey(e => e.Id);

        // (org_id, id) alternate key — target of journal_lines' composite dimension FK (ADR-013, P60).
        builder.HasAlternateKey(e => new { e.OrgId, e.Id });

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.DisplayName).IsRequired();
        builder.Property(e => e.ContactEmail);
        builder.Property(e => e.ContactPhone);
        builder.Property(e => e.Status).IsRequired().HasConversion<TenantStatusConverter>();
        builder.Property(e => e.IsSystem).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.CreatedAt).IsRequired();

        builder.HasIndex(e => new { e.OrgId, e.DisplayName });
        // GIN trigram index on display_name added in the AddDirectory migration (raw SQL).
    }
}
