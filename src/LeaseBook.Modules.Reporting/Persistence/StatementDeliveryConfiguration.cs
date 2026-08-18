using LeaseBook.Modules.Reporting.Delivery;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Reporting.Persistence;

/// <summary>
/// EF configuration for the statement-delivery history (ADR-040): the immutable
/// <see cref="StatementArtifact"/>, the <see cref="StatementDeliveryAttempt"/>s that send it, and the
/// append-only <see cref="StatementDeliveryEvent"/>s that record what became of each attempt.
/// <para>
/// Snake_case table/column names are applied globally by <c>UseSnakeCaseNamingConvention()</c>; only
/// the CHECK constraints, the converter and the composite org-carrying FKs need explicit wiring.
/// </para>
/// </summary>
public sealed class StatementArtifactConfiguration : IEntityTypeConfiguration<StatementArtifact>
{
    public void Configure(EntityTypeBuilder<StatementArtifact> builder)
    {
        builder.ToTable("statement_artifacts", t =>
            t.HasCheckConstraint(
                "ck_statement_artifacts_basis",
                "basis IS NULL OR basis IN ('cash', 'accrual')"));

        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.OwnerId).IsRequired();
        builder.Property(e => e.PeriodYear).IsRequired();
        builder.Property(e => e.PeriodMonth).IsRequired();
        builder.Property(e => e.Basis);
        builder.Property(e => e.ArtifactKey).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();

        // The common query: every artifact rendered for an org + owner in a period.
        builder.HasIndex(e => new { e.OrgId, e.OwnerId, e.PeriodYear, e.PeriodMonth });

        // Alternate key so attempts can carry a composite (org_id, artifact_id) FK — an attempt must
        // not be able to reference an artifact belonging to another org (§ Organization Isolation).
        builder.HasAlternateKey(e => new { e.OrgId, e.Id });
    }
}

/// <inheritdoc cref="StatementArtifactConfiguration"/>
public sealed class StatementDeliveryAttemptConfiguration
    : IEntityTypeConfiguration<StatementDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<StatementDeliveryAttempt> builder)
    {
        builder.ToTable("statement_delivery_attempts");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.ArtifactId).IsRequired();
        builder.Property(e => e.ToEmail).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();

        // Attempts for one artifact, oldest first — the retry history for a statement.
        builder.HasIndex(e => new { e.OrgId, e.ArtifactId, e.CreatedAt });

        builder.HasOne(e => e.Artifact)
            .WithMany(a => a.Attempts)
            .HasForeignKey(e => new { e.OrgId, e.ArtifactId })
            .HasPrincipalKey(a => new { a.OrgId, a.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasAlternateKey(e => new { e.OrgId, e.Id });
    }
}

/// <inheritdoc cref="StatementArtifactConfiguration"/>
public sealed class StatementDeliveryEventConfiguration
    : IEntityTypeConfiguration<StatementDeliveryEvent>
{
    public void Configure(EntityTypeBuilder<StatementDeliveryEvent> builder)
    {
        builder.ToTable("statement_delivery_events", t =>
        {
            t.HasCheckConstraint(
                "ck_statement_delivery_events_kind",
                $"kind IN ({Quote(DeliveryEventKindConverter.DbValues)})");

            // Sequence is 1-based and the Queued event is always 1 — a zero or negative position
            // would make DeliveryStatus.Of's ordering meaningless.
            t.HasCheckConstraint(
                "ck_statement_delivery_events_sequence",
                "sequence >= 1");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.OrgId).IsRequired();
        builder.Property(e => e.AttemptId).IsRequired();
        builder.Property(e => e.Sequence).IsRequired();
        builder.Property(e => e.Kind).IsRequired().HasConversion<DeliveryEventKindConverter>();
        builder.Property(e => e.OccurredAt).IsRequired();
        builder.Property(e => e.ProviderMessageId);
        builder.Property(e => e.Detail);
        builder.Property(e => e.CreatedAt).IsRequired();

        // Uniqueness is the ordering guarantee: two events cannot claim the same position in one
        // attempt's history, so "the latest event" is always a single row. A concurrent second
        // recorder loses on this index rather than silently interleaving.
        builder.HasIndex(e => new { e.OrgId, e.AttemptId, e.Sequence }).IsUnique();

        builder.HasOne(e => e.Attempt)
            .WithMany(a => a.Events)
            .HasForeignKey(e => new { e.OrgId, e.AttemptId })
            .HasPrincipalKey(a => new { a.OrgId, a.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static string Quote(string[] values) =>
        string.Join(", ", values.Select(v => $"'{v}'"));
}
