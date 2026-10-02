using LeaseBook.Modules.Payments.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Payments.Persistence;

public sealed class RefundCheckConfiguration : IEntityTypeConfiguration<RefundCheck>
{
    public void Configure(EntityTypeBuilder<RefundCheck> b)
    {
        b.ToTable("refund_checks", t =>
        {
            t.HasCheckConstraint("ck_refund_checks_amount", "amount > 0");
            t.HasCheckConstraint("ck_refund_checks_check_number", "check_number > 0");
            t.HasCheckConstraint("ck_refund_checks_source", "source IN ('deposit','prepayment')");
        });
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.OrgId, x.Id }).HasName("ak_refund_checks_org_id_id");
        b.HasIndex(x => new { x.OrgId, x.Key }).IsUnique();
        b.HasIndex(x => new { x.OrgId, x.BankAccountId, x.CheckNumber }).IsUnique();
        b.HasIndex(x => new { x.OrgId, x.EntryId }).IsUnique();
        b.HasIndex(x => new { x.OrgId, x.TenantId });
        b.Property(x => x.Amount).HasPrecision(14, 2);
        b.Property(x => x.Source).HasMaxLength(16);
        b.Property(x => x.PayeeName).HasMaxLength(120);
        b.Property(x => x.AddressLine1).HasMaxLength(120);
        b.Property(x => x.AddressLine2).HasMaxLength(120);
        b.Property(x => x.City).HasMaxLength(60);
        b.Property(x => x.State).HasMaxLength(2);
        b.Property(x => x.PostalCode).HasMaxLength(10);
        b.Property(x => x.Memo).HasMaxLength(60);
    }
}

public sealed class RefundCheckPrintConfiguration : IEntityTypeConfiguration<RefundCheckPrint>
{
    public void Configure(EntityTypeBuilder<RefundCheckPrint> b)
    {
        b.ToTable("refund_check_prints");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.OrgId, x.CheckId });
        b.HasOne<RefundCheck>().WithMany().HasForeignKey(x => new { x.OrgId, x.CheckId })
            .HasPrincipalKey(x => new { x.OrgId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CheckPrintSettingConfiguration : IEntityTypeConfiguration<CheckPrintSetting>
{
    public void Configure(EntityTypeBuilder<CheckPrintSetting> b)
    {
        b.ToTable("check_print_settings", t =>
        {
            t.HasCheckConstraint("ck_check_print_settings_offset_x", "offset_x_points BETWEEN -72 AND 72");
            t.HasCheckConstraint("ck_check_print_settings_offset_y", "offset_y_points BETWEEN -72 AND 72");
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.OrgId, x.BankAccountId }).IsUnique();
        b.Property(x => x.OffsetXPoints).HasPrecision(6, 2);
        b.Property(x => x.OffsetYPoints).HasPrecision(6, 2);
    }
}

public sealed class BankMicrProfileConfiguration : IEntityTypeConfiguration<BankMicrProfile>
{
    public void Configure(EntityTypeBuilder<BankMicrProfile> b)
    {
        b.ToTable("bank_micr_profiles", t =>
        {
            t.HasCheckConstraint("ck_bank_micr_profiles_stock_kind", "stock_kind IN ('preprinted', 'blank')");
            t.HasCheckConstraint("ck_bank_micr_profiles_micr_offset_x", "micr_offset_x_points BETWEEN -18 AND 18");
            t.HasCheckConstraint("ck_bank_micr_profiles_micr_offset_y", "micr_offset_y_points BETWEEN -18 AND 18");
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.OrgId, x.BankAccountId }).IsUnique();
        b.Property(x => x.StockKind).HasMaxLength(16);
        // Ciphertext from the host's Data Protection converter: unbounded text, never a plaintext width.
        b.Property(x => x.RoutingNumber).HasColumnType("text");
        b.Property(x => x.OnUsAccountNumber).HasColumnType("text");
        b.Property(x => x.MicrOffsetXPoints).HasPrecision(6, 2);
        b.Property(x => x.MicrOffsetYPoints).HasPrecision(6, 2);
    }
}
