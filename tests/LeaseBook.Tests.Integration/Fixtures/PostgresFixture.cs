using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace LeaseBook.Tests.Integration.Fixtures;

/// <summary>
/// One Postgres 18 container per test run. Runs the same <c>bootstrap.sql</c> as local compose
/// (so the three roles and grants are identical), applies migrations as the <b>migrator</b> role,
/// and exposes both the <b>app-role</b> and <b>migrator-role</b> connection strings. The isolation
/// pack (WP-05) drives all of its assertions through the app role — never the migrator — because
/// RLS does not constrain the table owner (pitfall E2).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("postgres")
        .WithUsername("postgres")
        .WithPassword("dev_postgres_pw")
        .WithResourceMapping(
            new FileInfo(Path.Combine(AppContext.BaseDirectory, "bootstrap.sql")),
            "/docker-entrypoint-initdb.d/")
        .Build();

    public string MigratorConnectionString { get; private set; } = string.Empty;

    public string AppConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(5432);
        MigratorConnectionString = ConnectionString(host, port, "leasebook_migrator", "dev_migrator_pw");
        AppConnectionString = ConnectionString(host, port, "leasebook_app", "dev_app_pw");

        await using var migratorDb = CreateContext(MigratorConnectionString);
        await migratorDb.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Builds an AppDbContext on the given role's connection (snake_case, like the host).</summary>
    public AppDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.SetPostgresVersion(18, 0))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options);
    }

    private static string ConnectionString(string host, int port, string user, string password) =>
        $"Host={host};Port={port};Database=leasebook;Username={user};Password={password};Include Error Detail=true";
}
