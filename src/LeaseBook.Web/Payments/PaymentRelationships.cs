using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Web.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Web.Payments;

// Only the host can name both ends of these composite references.
public sealed class PaymentOperationRelationships : IEntityTypeConfiguration<PaymentOperation>
{
    public void Configure(EntityTypeBuilder<PaymentOperation> b)
    {
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => new { x.OrgId, x.TenantId })
            .HasPrincipalKey(x => new { x.OrgId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => new { x.OrgId, x.UserId })
            .HasPrincipalKey(x => new { x.OrgId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BankAccount>().WithMany().HasForeignKey(x => new { x.OrgId, x.BankId })
            .HasPrincipalKey(x => new { x.OrgId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.OrgId, x.JournalId })
            .HasPrincipalKey(x => new { x.OrgId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class PaymentFixtureRelationships : IEntityTypeConfiguration<PaymentFixture>
{
    public void Configure(EntityTypeBuilder<PaymentFixture> b) =>
        b.HasOne<BankAccount>().WithMany().HasForeignKey(x => new { x.OrgId, x.BankId })
            .HasPrincipalKey(x => new { x.OrgId, x.Id }).OnDelete(DeleteBehavior.Restrict);
}
public sealed class PaymentEffectRelationships : IEntityTypeConfiguration<PaymentEffect>
{
    public void Configure(EntityTypeBuilder<PaymentEffect> b) =>
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.OrgId, x.JournalId })
            .HasPrincipalKey(x => new { x.OrgId, x.Id }).OnDelete(DeleteBehavior.Restrict);
}
