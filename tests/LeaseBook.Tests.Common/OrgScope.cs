using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Persistence;

// Testcontainers pulls in BouncyCastle, whose root namespace `Org` shadows the entity type.
using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Common;

/// <summary>
/// The standard harness entry point for container-backed accounting tests (P29). Each scope is a
/// <b>fresh org</b> — created via a migrator insert into <c>orgs</c> — so generated/property cases are
/// disjoint by RLS and need no cleanup (the append-only journal tables can't be truncated by the app
/// role anyway, pitfall M-E1). It hands back the org id, an app-role <see cref="AppDbContext"/> bound to
/// a live <see cref="TenantContext"/>, and an <see cref="OrgScopedExecutor"/> over them; <see cref="RunAsync"/>
/// is the convenience wrapper that opens the unit-of-work transaction and sets <c>app.org_id</c>.
/// </summary>
public sealed class OrgScope : IAsyncDisposable
{
    private OrgScope(Guid orgId, AppDbContext db, TenantContext tenant, OrgScopedExecutor executor)
    {
        OrgId = orgId;
        Db = db;
        Tenant = tenant;
        Executor = executor;
    }

    public Guid OrgId { get; }

    /// <summary>App-role context (RLS-subject), bound to <see cref="Tenant"/>.</summary>
    public AppDbContext Db { get; }

    public TenantContext Tenant { get; }

    public OrgScopedExecutor Executor { get; }

    public static async Task<OrgScope> CreateAsync(PostgresFixture fixture, CancellationToken ct, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = name ?? $"Test Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var tenant = new TenantContext();
        var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        return new OrgScope(orgId, db, tenant, new OrgScopedExecutor(db, tenant));
    }

    /// <summary>Runs <paramref name="work"/> inside this org's unit-of-work transaction (sets <c>app.org_id</c>).</summary>
    public Task RunAsync(Func<Task> work, CancellationToken ct = default) => Executor.RunAsync(OrgId, work, ct);

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
