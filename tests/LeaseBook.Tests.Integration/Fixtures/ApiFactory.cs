using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LeaseBook.Tests.Integration.Fixtures;

/// <summary>
/// Boots the real host (<c>Program</c>) against the shared Postgres container, connecting as the
/// RLS-subject <b>app role</b> — the same role production uses — so auth + tenancy flows are exercised
/// end-to-end rather than bypassed. Migrations are already applied (as migrator) by the fixture before
/// this factory is built.
/// </summary>
public sealed class ApiFactory(string appConnectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            // Point the app's DbContext at the test container (app role) instead of local compose.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.AddDbContext<AppDbContext>(options => options
                .UseNpgsql(appConnectionString, npgsql => npgsql.SetPostgresVersion(18, 0))
                .UseSnakeCaseNamingConvention());
        });
    }
}
