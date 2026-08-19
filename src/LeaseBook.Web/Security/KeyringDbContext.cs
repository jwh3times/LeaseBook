using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Security;

/// <summary>
/// The Data Protection keyring's own context (F8 / ADR-041). It maps exactly one table,
/// <c>data_protection_keys</c>, and depends on nothing but its options.
/// <para>
/// <b>It is deliberately not <see cref="Persistence.AppDbContext"/>,</b> for two independent reasons —
/// either alone would be disqualifying:
/// </para>
/// <list type="number">
/// <item>
/// <b>Circular construction.</b> <c>AppDbContext</c> takes an <c>IDataProtectionProvider</c> to build
/// the Identity token-store encryption converter. Persisting the keyring into it would make the
/// provider depend on the context that depends on the provider.
/// </item>
/// <item>
/// <b>ADR-039 would refuse the write.</b> <c>AppDbContext</c> rejects a write that declares no actor,
/// and key rotation has none — it is not a unit of work anyone is accountable for. Routing the
/// keyring through it would either fail at rotation or force a hole in the attribution rule.
/// </item>
/// </list>
/// <para>
/// The table itself is declared on <c>AppDbContext</c>'s model instead, so the single migration
/// history owns it and the schema guard can see it. This context only reads and writes rows.
/// </para>
/// </summary>
public sealed class KeyringDbContext(DbContextOptions<KeyringDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Must match the mapping AppDbContext declares for the same table, since that is what the
        // migration creates. DataProtectionKeyConfiguration is the single source of both.
        modelBuilder.ApplyConfiguration(new DataProtectionKeyConfiguration());
    }
}
