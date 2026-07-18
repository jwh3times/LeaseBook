using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace LeaseBook.Tests.Integration.Security;

public sealed class CookieSecurePolicyTests
{
    [Theory]
    // Environments.Development/Production are `static readonly`, not `const` (verified via
    // reflection: IsLiteral=false), so InlineData needs their literal values directly (CS0182).
    [InlineData("Development", CookieSecurePolicy.SameAsRequest)]
    [InlineData("Production", CookieSecurePolicy.Always)]
    public void Cookie_secure_policy_is_environment_driven(string environmentName, CookieSecurePolicy expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        // A DbContext is required by AddIdentity's EF stores; it need not connect for options resolution.
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql("Host=localhost;Database=none"));
        services.AddLeaseBookIdentity(new StubEnvironment(environmentName));

        using var provider = services.BuildServiceProvider();
        var cookie = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        cookie.Cookie.SecurePolicy.ShouldBe(expected);
    }

    private sealed class StubEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "test";
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
