using LeaseBook.Web.Persistence;
using LeaseBook.Web.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LeaseBook.Tests.Common;

/// <summary>
/// Boots the real host (<c>Program</c>) against the shared Postgres container, connecting as the
/// RLS-subject <b>app role</b> — the same role production uses — so auth + tenancy flows are exercised
/// end-to-end rather than bypassed. Migrations are already applied (as migrator) by the fixture before
/// this factory is built.
/// </summary>
/// <param name="settings">
/// Optional host-configuration overrides applied before <c>Program</c> reads configuration — the only
/// way to exercise a code path Program gates on config, such as WP-11's <c>Jobs:Enabled</c>. A test
/// that enables jobs must also point <c>ConnectionStrings:Default</c> at the container, because
/// Hangfire resolves its own storage connection from configuration rather than from the DbContext
/// replaced below, and would otherwise reach for the developer's local database.
/// </param>
public sealed class ApiFactory(
    string appConnectionString,
    IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        QuietConsole(builder);

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            // Point the app's DbContext at the test container (app role) instead of local compose.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.AddDbContext<AppDbContext>(options => options
                .UseNpgsql(appConnectionString, npgsql => npgsql.SetPostgresVersion(18, 0))
                .UseSnakeCaseNamingConvention());

            // The Data Protection keyring context (F8 / ADR-041) needs the same redirection, and for
            // the same reason as the Hangfire caveat above: Program registers it from
            // ConnectionStrings:Default, so without this the booted host encrypts against the
            // developer's local database while every other read goes to the container. That is not a
            // test-only wart — it silently splits the keyring from the data it protects.
            services.RemoveAll<DbContextOptions<KeyringDbContext>>();
            services.AddDbContext<KeyringDbContext>(options => options
                .UseNpgsql(appConnectionString, npgsql => npgsql.SetPostgresVersion(18, 0))
                .UseSnakeCaseNamingConvention());
        });
    }

    /// <summary>
    /// Raises the console provider's level to Warning for a booted test host (#548). At Information
    /// the host logs every SQL command, about 1 GB of standard output over the integration suite, and
    /// <c>dotnet test</c> holds a test process's whole output in one string, which .NET caps near
    /// 1.07 billion characters: past it the CLI throws <c>OutOfMemoryException</c> and exits 1 with
    /// every test green. Only the console provider is filtered. The log level itself is unchanged, so a
    /// test that adds its own provider to capture log events still sees them. It is applied before a
    /// test's own settings, which can still override it.
    /// </summary>
    public static void QuietConsole(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("Logging:Console:LogLevel:Default", "Warning");
    }
}
