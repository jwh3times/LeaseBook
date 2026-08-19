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
}
