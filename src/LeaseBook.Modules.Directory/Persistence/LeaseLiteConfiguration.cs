using LeaseBook.Modules.Directory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Directory.Persistence;

public sealed class LeaseLiteConfiguration : IEntityTypeConfiguration<LeaseLite>
{
    public void Configure(EntityTypeBuilder<LeaseLite> builder)
    {
        builder.ToTable("lease_lite", t =>
            t.HasCheckConstraint("ck_lease_lite_status", $"status IN ({DirectorySql.Quote(LeaseStatusConverter.DbValues)})"));

        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.UnitId).IsRequired();
        builder.Property(e => e.StartDate);
        builder.Property(e => e.EndDate);
        builder.Property(e => e.Rent).IsRequired();
        builder.Property(e => e.DepositRequired).IsRequired().HasDefaultValueSql("0");
        builder.Property(e => e.Status).IsRequired().HasConversion<LeaseStatusConverter>();
        builder.Property(e => e.CreatedAt).IsRequired();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Unit>().WithMany().HasForeignKey(e => e.UnitId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.OrgId, e.TenantId });
        builder.HasIndex(e => new { e.OrgId, e.UnitId });
    }
}
