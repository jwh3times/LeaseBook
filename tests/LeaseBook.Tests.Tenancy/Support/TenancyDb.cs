using System.Data.Common;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

// Testcontainers pulls in BouncyCastle, whose root namespace `Org` shadows the entity type.
using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Tenancy.Support;

/// <summary>
/// What a session can see of the context the code under test set: the three organization-plane
/// settings, the platform one, and which server process answered. Every value is read with
/// <c>current_setting(…, true)</c>, which answers NULL for a setting this session has never had
/// and '' for one a transaction set and then took back.
/// </summary>
internal sealed record SessionContext(string? Org, string? Persona, string? User, string? Platform, int BackendPid);

/// <summary>
/// One app-role connection held open for a whole test, with an <see cref="AppDbContext"/>, the two
/// request-scoped contexts and an <see cref="OrgScopedExecutor"/> over it, and a recorder of every
/// transaction the context begins, commits or rolls back.
/// <para>
/// The connection is unpooled and opened here, so it is a server process of its own that no other
/// test has touched, and EF never closes it between units of work. That makes "the setting did not
/// survive the transaction" a statement about this one session, not about whichever pooled
/// connection the next statement happened to get.
/// </para>
/// </summary>
internal sealed class Session : IAsyncDisposable
{
    private Session(NpgsqlConnection connection, AppDbContext db, OrgContext org, ActorContext actor,
        TransactionRecorder transactions)
    {
        Connection = connection;
        Db = db;
        Org = org;
        Actor = actor;
        Transactions = transactions;
        Executor = new OrgScopedExecutor(db, org, actor);
    }

    public NpgsqlConnection Connection { get; }

    public AppDbContext Db { get; }

    public OrgContext Org { get; }

    public ActorContext Actor { get; }

    public OrgScopedExecutor Executor { get; }

    public TransactionRecorder Transactions { get; }

    public static async Task<Session> OpenAsync(PostgresFixture fixture, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.AppConnectionString)
        {
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync(ct);

        var transactions = new TransactionRecorder();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.SetPostgresVersion(18, 0))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(transactions)
            .Options;
        var org = new OrgContext();
        var actor = new ActorContext();
        return new Session(connection, new AppDbContext(options, org, actor), org, actor, transactions);
    }

    /// <summary>What this session sees now, on the context's open transaction if it has one.</summary>
    public Task<SessionContext> ReadAsync(CancellationToken ct) => TenancyDb.ReadAsync(Db, ct);

    /// <summary>Writes one <c>audit_events</c> row for <paramref name="orgId"/> on the open transaction.</summary>
    public Task InsertEventAsync(Guid orgId, CancellationToken ct) =>
        RlsProbe.InsertEventAsync(Connection, TenancyDb.TransactionOf(Db), orgId, ct);

    /// <summary>How many <c>audit_events</c> rows this session can read on the open transaction.</summary>
    public Task<long> CountEventsAsync(CancellationToken ct) =>
        RlsProbe.CountEventsAsync(Connection, TenancyDb.TransactionOf(Db), ct);

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

internal static class TenancyDb
{
    /// <summary>A new organization, inserted as the migrator, as every container suite makes one.</summary>
    public static async Task<Guid> NewOrgAsync(PostgresFixture fixture, CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Tenancy Org {orgId:N}" });
        await migratorDb.SaveChangesAsync(ct);
        return orgId;
    }

    /// <summary>
    /// Plants <paramref name="count"/> <c>audit_events</c> rows for <paramref name="orgId"/> through
    /// the probe, on a connection of its own, and commits them.
    /// </summary>
    public static async Task PlantEventsAsync(PostgresFixture fixture, Guid orgId, int count, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);
        for (var i = 0; i < count; i++)
        {
            await RlsProbe.InsertEventAsync(conn, tx, orgId, ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// How many <c>audit_events</c> rows <paramref name="orgId"/> holds, read org-wide on a
    /// connection of its own: what another request would see once the unit of work has ended.
    /// </summary>
    public static async Task<long> CommittedEventsAsync(PostgresFixture fixture, Guid orgId, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);
        return await RlsProbe.CountEventsAsync(conn, tx, ct);
    }

    public static NpgsqlTransaction? TransactionOf(DbContext db) =>
        db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;

    public static async Task<SessionContext> ReadAsync(DbContext db, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
            opened = true;
        }

        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT current_setting('app.org_id', true), current_setting('app.persona', true), " +
                "current_setting('app.user_id', true), current_setting('app.platform', true), pg_backend_pid()",
                conn,
                TransactionOf(db));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return new SessionContext(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4));
        }
        finally
        {
            if (opened)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}

/// <summary>
/// Records the transaction calls EF makes through its interceptor seam, in order. An explicit
/// rollback reaches <see cref="TransactionRolledBackAsync"/>; a transaction that is merely disposed
/// without one does not, so the recorder tells the two apart.
/// </summary>
internal sealed class TransactionRecorder : DbTransactionInterceptor
{
    private readonly List<string> _events = [];

    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>Lets a test put its own steps in the same sequence as the transaction calls.</summary>
    public void Note(string step)
    {
        lock (_events)
        {
            _events.Add(step);
        }
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection,
        TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        Note("begin");
        return ValueTask.FromResult(result);
    }

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Note("commit");
        return Task.CompletedTask;
    }

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Note("rollback");
        return Task.CompletedTask;
    }
}
