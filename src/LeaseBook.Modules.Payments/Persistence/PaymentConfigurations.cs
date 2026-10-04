using LeaseBook.Modules.Payments.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Payments.Persistence;

public sealed class PaymentFixtureConfiguration : IEntityTypeConfiguration<PaymentFixture>
{
    public void Configure(EntityTypeBuilder<PaymentFixture> b)
    {
        b.ToTable("payment_fixtures"); b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.OrgId, x.Id }).HasName("ak_payment_fixtures_org_id_id");
        b.HasIndex(x => x.OrgId).IsUnique();
        b.Property(x => x.Account).HasMaxLength(100);
    }
}

public sealed class PaymentOperationConfiguration : IEntityTypeConfiguration<PaymentOperation>
{
    public void Configure(EntityTypeBuilder<PaymentOperation> b)
    {
        b.ToTable("payment_operations", t =>
        {
            t.HasCheckConstraint("ck_payment_operation_amount", "amount > 0 AND amount <= 10000");
            t.HasCheckConstraint("ck_payment_operation_currency", "currency = 'USD'");
            t.HasCheckConstraint("ck_payment_operation_status", "status IN ('Requested','Processing','Failed','Settled','NeedsReview','Returned','ReviewClosed')");
        });
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.OrgId, x.Id }).HasName("ak_payment_operations_org_id_id");
        b.HasIndex(x => new { x.OrgId, x.UserId, x.Key }).IsUnique();
        b.HasIndex(x => new { x.OrgId, x.Account, x.ProviderId }).IsUnique();
        b.HasIndex(x => new { x.OrgId, x.TenantId, x.CreatedAt });
        b.HasIndex(x => new { x.OrgId, x.DueAt });
        b.HasOne<PaymentFixture>().WithMany().HasForeignKey(x => new { x.OrgId, x.Generation })
            .HasPrincipalKey(x => new { x.OrgId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Amount).HasPrecision(14, 2);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Account).HasMaxLength(100);
        b.Property(x => x.ProviderId).HasMaxLength(100);
        b.Property(x => x.Fingerprint).HasMaxLength(64);
        b.Property(x => x.Status).HasMaxLength(20);
        b.Property(x => x.Reason).HasMaxLength(60);
        b.Property(x => x.ReviewNote).HasMaxLength(500);
    }
}

public sealed class PaymentObservationConfiguration : IEntityTypeConfiguration<PaymentObservation>
{
    public void Configure(EntityTypeBuilder<PaymentObservation> b)
    {
        b.ToTable("payment_observations"); b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.OrgId, x.Id }).HasName("ak_payment_observations_org_id_id");
        b.HasIndex(x => new { x.OrgId, x.Account, x.Mode, x.EventId }).IsUnique();
        b.HasIndex(x => new { x.OrgId, x.ProviderId });
        b.Property(x => x.Gross).HasPrecision(14, 2);
        b.Property(x => x.Fee).HasPrecision(14, 2);
        b.Property(x => x.Net).HasPrecision(14, 2);
        foreach (var name in new[] { "EventId", "ProviderId", "Account", "EvidenceId", "PayoutId" })
        { b.Property<string>(name).HasMaxLength(100); }
        b.Property(x => x.Fingerprint).HasMaxLength(64);
        b.Property(x => x.Kind).HasMaxLength(40);
        b.Property(x => x.Mode).HasMaxLength(20);
        b.Property(x => x.Currency).HasMaxLength(3);
    }
}

public sealed class PaymentEffectConfiguration : IEntityTypeConfiguration<PaymentEffect>
{
    public void Configure(EntityTypeBuilder<PaymentEffect> b)
    {
        b.ToTable("payment_effects"); b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.OrgId, x.OperationId, x.Kind }).IsUnique();
        b.Property(x => x.Kind).HasMaxLength(20);
        b.HasOne<PaymentOperation>().WithMany().HasForeignKey(x => new { x.OrgId, x.OperationId })
            .HasPrincipalKey(x => new { x.OrgId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PaymentObservation>().WithMany().HasForeignKey(x => new { x.OrgId, x.ObservationId })
            .HasPrincipalKey(x => new { x.OrgId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SimulatedCollectionConfiguration : IEntityTypeConfiguration<SimulatedCollection>
{
    public void Configure(EntityTypeBuilder<SimulatedCollection> b)
    {
        b.ToTable("simulated_collections"); b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.OrgId, x.ProviderId }).IsUnique();
        b.Property(x => x.ProviderId).HasMaxLength(100);
        b.Property(x => x.Fingerprint).HasMaxLength(64);
    }
}
