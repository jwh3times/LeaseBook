using System.Reflection;
using System.Text.Json;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace LeaseBook.Web.Persistence;

/// <summary>
/// The single application DbContext (ADR-004). Discovers entity configurations from the host and
/// every module, applies snake_case naming + UTC timestamptz + the Money converter conventions,
/// and stamps <c>created_at</c> on insert.
/// <para>
/// Tenancy (WP-05) is layered on as <b>ergonomics over the RLS boundary</b>: a global query filter
/// scopes every <see cref="IOrgScoped"/> entity to the current <see cref="ITenantContext"/>, and the
/// SaveChanges pass stamps <c>org_id</c> on inserts, refuses cross-org writes, and emits one
/// <see cref="AuditEvent"/> per change. None of this replaces the Postgres policy — it just fails
/// loudly in-process before a bug reaches the database.
/// </para>
/// </summary>
public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ITenantContext? tenantContext = null,
    IActorContext? actorContext = null)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    private readonly ITenantContext _tenant = tenantContext ?? NullTenantContext.Instance;

    // The acting user for audit stamping (P52). Null for the seeder/jobs and any context built without
    // one (tests, design-time) — a system write, stamped null without throwing.
    private readonly IActorContext? _actor = actorContext;

    public DbSet<Org> Orgs => Set<Org>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // EFCore.NamingConventions snake_cases Identity's columns and key names but leaves the table
        // names PascalCase; map them explicitly so the whole schema is snake_case (§C.3). These are
        // identity-class (no RLS, pitfall E6) and are allowlisted in the schema guard.
        modelBuilder.Entity<AppUser>().ToTable("asp_net_users");
        modelBuilder.Entity<IdentityRole<Guid>>().ToTable("asp_net_roles");
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("asp_net_user_claims");
        modelBuilder.Entity<IdentityUserRole<Guid>>().ToTable("asp_net_user_roles");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("asp_net_user_logins");
        modelBuilder.Entity<IdentityRoleClaim<Guid>>().ToTable("asp_net_role_claims");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("asp_net_user_tokens");

        foreach (var assembly in PersistenceAssemblies.ModelAssemblies)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }

        // One convention, not per-entity copy-paste (pitfall E9): every IOrgScoped entity gets the
        // same org filter bound to the live tenant context. null OrgId → org_id = NULL → no rows.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(IOrgScoped).IsAssignableFrom(entityType.ClrType))
            {
                typeof(AppDbContext)
                    .GetMethod(nameof(SetOrgFilter), BindingFlags.Instance | BindingFlags.NonPublic)!
                    .MakeGenericMethod(entityType.ClrType)
                    .Invoke(this, [modelBuilder]);
            }
        }
    }

    private void SetOrgFilter<TEntity>(ModelBuilder modelBuilder) where TEntity : class, IOrgScoped =>
        modelBuilder.Entity<TEntity>().HasQueryFilter(e => e.OrgId == _tenant.OrgId);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // Money is always NUMERIC(14,2) (CLAUDE.md). All timestamps are UTC timestamptz.
        configurationBuilder.Properties<Money>().HaveConversion<MoneyConverter>().HavePrecision(14, 2);
        configurationBuilder.Properties<DateTime>().HaveColumnType("timestamp with time zone");
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyTenancyAndAudit();
        StampCreatedAt();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        ApplyTenancyAndAudit();
        StampCreatedAt();
        return base.SaveChanges();
    }

    /// <summary>
    /// Stamps <c>org_id</c> on new org-scoped rows, rejects any write that crosses orgs, and queues
    /// one append-only <see cref="AuditEvent"/> per change. Snapshots the change set first so the
    /// audit rows we add are not themselves audited (and audit rows are skipped outright).
    /// </summary>
    private void ApplyTenancyAndAudit()
    {
        ChangeTracker.DetectChanges();

        var changes = ChangeTracker.Entries()
            .Where(e => e.Entity is IOrgScoped
                        && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        if (changes.Count == 0)
        {
            return;
        }

        var orgId = _tenant.OrgId
            ?? throw new InvalidOperationException(
                "An org-scoped write was attempted with no tenant context. Org-scoped DB work must " +
                "run inside the request middleware or OrgScopedExecutor, which set app.org_id (§C.4).");

        var audits = new List<AuditEvent>(changes.Count);
        foreach (var entry in changes)
        {
            var scoped = (IOrgScoped)entry.Entity;

            // Stamp/guard every org-scoped row — including audit rows — so nothing crosses orgs.
            if (entry.State == EntityState.Added)
            {
                if (scoped.OrgId == Guid.Empty)
                {
                    scoped.OrgId = orgId;
                }
                else if (scoped.OrgId != orgId)
                {
                    throw CrossOrg(entry, scoped.OrgId, orgId);
                }
            }
            else if (scoped.OrgId != orgId)
            {
                // Modify/delete of a row owned by another org — RLS would reject it; fail earlier.
                throw CrossOrg(entry, scoped.OrgId, orgId);
            }

            // ...but never audit the audit log itself (no recursion; audit_events is append-only).
            if (entry.Entity is not AuditEvent)
            {
                audits.Add(BuildAuditEvent(entry, orgId, _actor?.UserId));
            }
        }

        AuditEvents.AddRange(audits);
    }

    private static InvalidOperationException CrossOrg(EntityEntry entry, Guid entityOrg, Guid contextOrg) =>
        new($"Cross-org write blocked: {entry.Entity.GetType().Name} carries org {entityOrg} but the " +
            $"current tenant context is {contextOrg}.");

    private static AuditEvent BuildAuditEvent(EntityEntry entry, Guid orgId, Guid? actorUserId)
    {
        var action = entry.State switch
        {
            EntityState.Added => "insert",
            EntityState.Modified => "update",
            EntityState.Deleted => "delete",
            _ => "unknown",
        };

        return new AuditEvent
        {
            Id = UuidV7.NewId(),
            OrgId = orgId,
            ActorUserId = actorUserId, // The acting user from the auth claim (P52); null for system writes.
            EntityType = entry.Metadata.GetTableName() ?? entry.Metadata.ClrType.Name,
            EntityId = entry.Property("Id").CurrentValue is Guid id ? id : Guid.Empty,
            Action = action,
            Before = entry.State is EntityState.Modified or EntityState.Deleted
                ? Serialize(entry.OriginalValues) : null,
            After = entry.State is EntityState.Added or EntityState.Modified
                ? Serialize(entry.CurrentValues) : null,
            OccurredAt = DateTime.UtcNow,
        };
    }

    private static string Serialize(PropertyValues values)
    {
        var snapshot = new Dictionary<string, object?>(values.Properties.Count);
        foreach (var property in values.Properties)
        {
            snapshot[property.Name] = values[property.Name];
        }

        return JsonSerializer.Serialize(snapshot);
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
