using LeaseBook.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Persistence;

/// <summary>
/// The single application DbContext (ADR-004). Discovers entity configurations from the host and
/// every module, applies snake_case naming + UTC timestamptz + the Money converter conventions,
/// and stamps <c>created_at</c> on insert. The tenancy query filter, org stamping, and audit
/// interceptor are layered on in WP-05.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Org> Orgs => Set<Org>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var assembly in PersistenceAssemblies.ModelAssemblies)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Money is always NUMERIC(14,2) (CLAUDE.md). All timestamps are UTC timestamptz.
        configurationBuilder.Properties<Money>().HaveConversion<MoneyConverter>().HavePrecision(14, 2);
        configurationBuilder.Properties<DateTime>().HaveColumnType("timestamp with time zone");
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampCreatedAt();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        StampCreatedAt();
        return base.SaveChanges();
    }

    private void StampCreatedAt()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added)
            {
                continue;
            }

            var createdAt = entry.Metadata.FindProperty("CreatedAt");
            if (createdAt is { ClrType: var clrType } && clrType == typeof(DateTime))
            {
                var property = entry.Property("CreatedAt");
                if (property.CurrentValue is null or DateTime { Ticks: 0 })
                {
                    property.CurrentValue = DateTime.UtcNow;
                }
            }
        }
    }
}
