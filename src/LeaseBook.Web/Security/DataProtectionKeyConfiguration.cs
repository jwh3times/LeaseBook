using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Web.Security;

/// <summary>
/// Mapping for <c>data_protection_keys</c> (F8 / ADR-041), applied by <b>both</b>
/// <see cref="Persistence.AppDbContext"/> — which owns the table in the migration history and the
/// schema guard — and <see cref="KeyringDbContext"/>, which is the only thing that reads or writes it
/// at runtime. One configuration so the two mappings cannot drift into disagreeing about the table
/// the migration actually created.
/// <para>
/// <b>Global-class:</b> the keyring belongs to the deployment, not to an organization, so the table
/// carries no <c>org_id</c>, gets no RLS policy, and is allowlisted in <c>SchemaGuardTests</c>
/// alongside <c>orgs</c> and <c>feature_flags</c>. It holds no organization data — only wrapped key
/// material and its metadata.
/// </para>
/// <para>
/// It is deliberately <b>not</b> append-only. Data Protection revokes a key by rewriting its XML, so
/// the runtime role keeps its default <c>UPDATE</c>/<c>DELETE</c> grants and no
/// <c>RevokeAppendOnly</c> call is made — unlike the journal and audit tables, this is operational
/// state rather than a fiduciary record.
/// </para>
/// </summary>
public sealed class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<DataProtectionKey> builder)
    {
        builder.ToTable("data_protection_keys");

        builder.HasKey(k => k.Id);

        // Names the framework's own key. Indexed because the keyring reads the whole table on every
        // refresh and this is what identifies an element.
        builder.Property(k => k.FriendlyName);

        // The serialized <key> element. Wrapped by a Key Vault key wherever the wrap is configured
        // (Production); plaintext XML otherwise, which is why dev and test never share a database
        // with anything that matters.
        builder.Property(k => k.Xml).IsRequired();
    }
}
