using LeaseBook.Modules.Capabilities.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Modules.Capabilities.Persistence;

/// <summary>
/// Maps <see cref="FeatureFlag"/> onto the hand-authored <c>feature_flags</c> table. The key is the
/// capability name, so a flag row and its registry entry line up without a surrogate id.
/// </summary>
public sealed class FeatureFlagConfiguration : IEntityTypeConfiguration<FeatureFlag>
{
    public void Configure(EntityTypeBuilder<FeatureFlag> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("feature_flags");
        builder.HasKey(f => f.Name);
        builder.Property(f => f.Name).IsRequired();
        builder.Property(f => f.Enabled).IsRequired();
        builder.Property(f => f.Description);
        builder.Property(f => f.UpdatedAt).IsRequired();
        builder.Property(f => f.UpdatedBy).IsRequired();
    }
}
