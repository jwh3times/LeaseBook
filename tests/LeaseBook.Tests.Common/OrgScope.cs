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

    /// <summary>
    /// The bank-account ids the accounting harness provisioned for <b>this org</b>. Since the composite
    /// <c>(org_id, bank_account_id)</c> dimension FK is org-scoped (ADR-013) and <c>bank_accounts.id</c> is
    /// globally unique, bank ids can no longer be fixed constants shared across test orgs — each scope
    /// gets its own, recorded here by <see cref="Tests.Common"/> consumers via <see cref="SetBankIds"/>.
    /// </summary>
    public Guid TrustBankId { get; private set; }

    public Guid DepositBankId { get; private set; }

    public Guid OperatingBankId { get; private set; }

    /// <summary>Records the per-org bank ids the harness provisioned (called once, during provisioning).</summary>
    public void SetBankIds(Guid trust, Guid deposit, Guid operating)
    {
        TrustBankId = trust;
        DepositBankId = deposit;
        OperatingBankId = operating;
    }

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
