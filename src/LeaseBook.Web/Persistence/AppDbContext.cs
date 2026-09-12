using System.Reflection;
using System.Text.Json;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Tenancy;
using Microsoft.AspNetCore.DataProtection;
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
/// scopes every <see cref="IOrgScoped"/> entity to the current <see cref="IOrgContext"/>, and the
/// SaveChanges pass stamps <c>org_id</c> on inserts, refuses cross-org writes, and emits one
/// <see cref="AuditEvent"/> per change. None of this replaces the Postgres policy — it just fails
/// loudly in-process before a bug reaches the database.
/// </para>
/// </summary>
public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    IOrgContext? orgContext = null,
    IActorContext? actorContext = null,
    IDataProtectionProvider? dataProtection = null)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    private readonly IOrgContext _org = orgContext ?? NullOrgContext.Instance;

    // Who is accountable, for audit stamping (P52). Absent only on contexts built outside DI
    // (design-time, the migrator) — which never write org-scoped rows. Since ADR-039 an org-scoped
    // write with no declared actor throws rather than stamping an unattributed row.
    private readonly IActorContext? _actor = actorContext;

    // F6: present only when the host resolves it via DI. The migrator/design-time/test-fixture paths
    // construct AppDbContext directly (no DI), so they get null → plaintext model — correct, because
    // migrations only create the column; they never write token values.
    private readonly IDataProtectionProvider? _dataProtection = dataProtection;

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

        // F6: encrypt the token store's Value column (TOTP authenticator key + 2FA recovery codes)
        // at rest. Only applied when a protector is present — see the ctor param comment above.
        if (_dataProtection is not null)
        {
            var converter = new EncryptedStringConverter(
                _dataProtection.CreateProtector("LeaseBook.IdentityTokens.v1"));
            modelBuilder.Entity<IdentityUserToken<Guid>>()
                .Property(t => t.Value)
                .HasConversion(converter);
        }

        foreach (var assembly in PersistenceAssemblies.ModelAssemblies)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }

        // One convention, not per-entity copy-paste (pitfall E9): every IOrgScoped entity gets the
        // same org filter bound to the live organization context. null OrgId → org_id = NULL → no rows.
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
        modelBuilder.Entity<TEntity>().HasQueryFilter(e => e.OrgId == _org.OrgId);

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

        var orgId = _org.OrgId
            ?? throw new InvalidOperationException(
                "An org-scoped write was attempted with no organization context. Org-scoped DB work must " +
                "run inside the request middleware or OrgScopedExecutor, which set app.org_id (§C.4).");

        // ADR-039: attribution is durable, so a missing actor is a bug to report here rather than a
        // null to persist. It is exactly as recoverable as the org context checked above — both are
        // set together by OrgScopedExecutor, and neither can be reconstructed from the row later.
        var actor = _actor?.Actor
            ?? throw new InvalidOperationException(
                "An org-scoped write was attempted with no declared actor. Org-scoped DB work must run " +
                "inside the request middleware or OrgScopedExecutor, which declares who is accountable " +
                "— Actor.User(id) for a signed-in user, Actor.System(process) for a named process " +
                "(ADR-039).");

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
            // A hand-written row gets its ADR-039 attribution stamped here instead, for the same
            // reason org_id is stamped above: the ambient actor is the only correct answer, and the
            // call site cannot improve on it by repeating it.
            if (entry.Entity is AuditEvent handWritten)
            {
                if (entry.State == EntityState.Added)
                {
                    StampAttribution(handWritten, actor);
                }
                else
                {
                    // audit_events is append-only and the runtime role holds no UPDATE/DELETE grant,
                    // so this would fail at commit as a bare Postgres 42501 naming no call site.
                    throw new InvalidOperationException(
                        $"An audit row was marked {entry.State} (id {handWritten.Id}). audit_events is " +
                        "append-only: a correction is a new row, never an edit of a posted one.");
                }
            }
            else
            {
                audits.Add(BuildAuditEvent(entry, orgId, actor));
            }
        }

        AuditEvents.AddRange(audits);
    }

    /// <summary>
    /// ADR-039 attribution for an audit row a feature wrote by hand. The auditing pass above builds its
    /// own rows from <see cref="Actor"/> already; this gives the hand-written ones the same three columns
    /// from the same source, so <c>actor_kind</c> cannot be omitted one initializer at a time.
    /// <para>
    /// The null branch of the <c>actor_kind</c> check constraint is reserved for rows that <i>predate</i>
    /// ADR-039 — the ones where a null genuinely cannot say whether a process acted or nobody was
    /// recorded. A row written today with a null kind is asserting something untrue about itself, and it
    /// is invisible to the audit-review surface's <c>System (automated)</c> filter, which keys on
    /// <c>actor_kind = 'system'</c>.
    /// </para>
    /// <para>
    /// A row that already carries the ambient actor is left as it is. One that carries a <i>different</i>
    /// actor is a bug at the call site rather than a disagreement to reconcile here: attribution names who
    /// did the work, and the unit of work has already declared that.
    /// </para>
    /// </summary>
    private static void StampAttribution(AuditEvent audit, Actor actor)
    {
        if (audit.ActorKind is null && audit.ActorUserId is null && audit.ActorProcess is null)
        {
            audit.ActorKind = actor.Kind;
            audit.ActorUserId = actor.UserId;
            audit.ActorProcess = actor.Process;
            return;
        }

        if (audit.ActorKind != actor.Kind
            || audit.ActorUserId != actor.UserId
            || audit.ActorProcess != actor.Process)
        {
            throw new InvalidOperationException(
                $"A hand-written audit row declares [{Describe(audit)}] but the unit of work is " +
                $"attributed to [{Describe(actor)}]. Leave actor_kind, actor_user_id and actor_process " +
                "unset and SaveChanges stamps all three from the ambient actor (ADR-039); set them and " +
                "they must be that same actor. A partial set is the case this exists to catch — it is " +
                "what writes a row claiming the null-actor_kind shape reserved for pre-ADR-039 rows.");
        }
    }

    /// <summary>
    /// All three columns, always — not the one the kind selects. Rendering
    /// <see cref="Actor.Reference"/> on each side would print the same string twice whenever the
    /// disagreement is in a field that reference does not carry: a row with the ambient kind and user
    /// but a stray <c>actor_process</c> is exactly such a case, and it is also one the check constraint
    /// rejects, so this message is the only readable diagnosis it will get.
    /// </summary>
    private static string Describe(AuditEvent audit) =>
        Describe(audit.ActorKind, audit.ActorUserId, audit.ActorProcess);

    private static string Describe(Actor actor) => Describe(actor.Kind, actor.UserId, actor.Process);

    private static string Describe(string? kind, Guid? userId, string? process) =>
        $"actor_kind={kind ?? "<unset>"}, actor_user_id={userId?.ToString() ?? "<unset>"}, " +
        $"actor_process={process ?? "<unset>"}";

    private static InvalidOperationException CrossOrg(EntityEntry entry, Guid entityOrg, Guid contextOrg) =>
        new($"Cross-org write blocked: {entry.Entity.GetType().Name} carries org {entityOrg} but the " +
            $"current organization context is {contextOrg}.");

    private static AuditEvent BuildAuditEvent(EntityEntry entry, Guid orgId, Actor actor)
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
            // The acting user from the auth claim (P52), null for a system write — with actor_kind
            // and actor_process saying which, so the two are distinguishable afterwards (ADR-039).
            ActorUserId = actor.UserId,
            ActorKind = actor.Kind,
            ActorProcess = actor.Process,
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
