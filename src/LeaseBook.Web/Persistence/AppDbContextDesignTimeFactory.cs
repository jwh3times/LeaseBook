using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace LeaseBook.Web.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> at design time. Connects as the <b>migrator</b> role (§C.5
/// ConnectionStrings:Migrations) — schema changes are never made by the runtime app role.
/// </summary>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("Migrations")
            ?? "Host=localhost;Port=5432;Database=leasebook;Username=leasebook_migrator;Password=dev_migrator_pw";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name)
                .SetPostgresVersion(18, 0))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}
