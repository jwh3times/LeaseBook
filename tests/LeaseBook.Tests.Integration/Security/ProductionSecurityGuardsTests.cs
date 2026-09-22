using LeaseBook.Web.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LeaseBook.Tests.Integration.Security;

public sealed class ProductionSecurityGuardsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    public void Development_environment_is_a_no_op_regardless_of_config(string? allowedHosts)
    {
        var config = BuildConfig(allowedHosts, durable: null);

        Should.NotThrow(() => ProductionSecurityGuards.Validate(config, new StubEnvironment("Development")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Non_development_with_empty_AllowedHosts_throws(string? allowedHosts)
    {
        var config = BuildConfig(allowedHosts, durable: true);

        var ex = Should.Throw<InvalidOperationException>(
            () => ProductionSecurityGuards.Validate(config, new StubEnvironment("Production")));
        ex.Message.ShouldContain("AllowedHosts");
    }

    [Fact]
    public void Non_development_with_wildcard_AllowedHosts_throws()
    {
        var config = BuildConfig("*", durable: true);

        var ex = Should.Throw<InvalidOperationException>(
            () => ProductionSecurityGuards.Validate(config, new StubEnvironment("Production")));
        ex.Message.ShouldContain("AllowedHosts");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void Non_development_with_real_host_needs_no_keyring_attestation(bool? durable)
    {
        var config = BuildConfig("leasebook.example.com", durable);

        Should.NotThrow(() => ProductionSecurityGuards.Validate(config, new StubEnvironment("Production")));
    }

    /// <summary>
    /// The modes that encrypt but authenticate nobody. <c>Prefer</c> is Npgsql's default, so the
    /// unspecified case and the explicitly-weak case are the same bug and are covered together.
    /// </summary>
    [Theory]
    [InlineData("Host=db;Database=leasebook;Username=u;Password=p", "Prefer")]
    [InlineData("Host=db;Database=leasebook;Username=u;Password=p;SSL Mode=Prefer", "Prefer")]
    [InlineData("Host=db;Database=leasebook;Username=u;Password=p;SSL Mode=Require", "Require")]
    [InlineData("Host=db;Database=leasebook;Username=u;Password=p;SSL Mode=Allow", "Allow")]
    [InlineData("Host=db;Database=leasebook;Username=u;Password=p;SSL Mode=Disable", "Disable")]
    public void Non_development_connection_string_that_does_not_verify_the_certificate_throws(
        string connectionString, string expectedMode)
    {
        var config = BuildConfig("leasebook.example.com", durable: true, connectionString);

        var ex = Should.Throw<InvalidOperationException>(
            () => ProductionSecurityGuards.Validate(config, new StubEnvironment("Production")));
        ex.Message.ShouldContain("ConnectionStrings:Default");
        ex.Message.ShouldContain(expectedMode);
        ex.Message.ShouldContain("VerifyFull");
    }

    [Theory]
    [InlineData("VerifyFull")]
    [InlineData("VerifyCA")]
    public void Non_development_connection_string_that_verifies_the_certificate_is_accepted(string mode)
    {
        var config = BuildConfig(
            "leasebook.example.com", durable: true,
            $"Host=db;Database=leasebook;Username=u;Password=p;SSL Mode={mode}");

        Should.NotThrow(() => ProductionSecurityGuards.Validate(config, new StubEnvironment("Production")));
    }

    /// <summary>
    /// The migrations string is checked too, and it is the one that matters most: it carries the
    /// schema-owner credential from a hosted CI runner over the public internet.
    /// </summary>
    [Fact]
    public void Non_development_migrations_connection_string_is_checked_independently()
    {
        var config = BuildConfig(
            "leasebook.example.com", durable: true,
            connectionString: "Host=db;Database=leasebook;Username=u;Password=p;SSL Mode=VerifyFull",
            migrationsConnectionString: "Host=db;Database=leasebook;Username=m;Password=p");

        var ex = Should.Throw<InvalidOperationException>(
            () => ProductionSecurityGuards.Validate(config, new StubEnvironment("Production")));
        ex.Message.ShouldContain("ConnectionStrings:Migrations");
    }

    /// <summary>
    /// Absent is not weak. Nothing to validate means the failure arrives as a connection error, not
    /// as silent exposure — and the Production-environment host tests boot with no connection string
    /// at all, because <c>ApiFactory</c> supplies the DbContext directly.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Non_development_with_no_connection_string_is_not_the_guard_s_business(string? connectionString)
    {
        var config = BuildConfig("leasebook.example.com", durable: true, connectionString);

        Should.NotThrow(() => ProductionSecurityGuards.Validate(config, new StubEnvironment("Production")));
    }

    [Fact]
    public void Development_ignores_a_connection_string_that_verifies_nothing()
    {
        var config = BuildConfig(
            "leasebook.example.com", durable: true,
            "Host=localhost;Database=leasebook;Username=u;Password=p");

        Should.NotThrow(() => ProductionSecurityGuards.Validate(config, new StubEnvironment("Development")));
    }

    private static IConfiguration BuildConfig(
        string? allowedHosts,
        bool? durable,
        string? connectionString = null,
        string? migrationsConnectionString = null)
    {
        var values = new Dictionary<string, string?>();
        if (connectionString is not null)
        {
            values["ConnectionStrings:Default"] = connectionString;
        }

        if (migrationsConnectionString is not null)
        {
            values["ConnectionStrings:Migrations"] = migrationsConnectionString;
        }

        if (allowedHosts is not null)
        {
            values["AllowedHosts"] = allowedHosts;
        }

        if (durable is not null)
        {
            values["DataProtection:Durable"] = durable.Value.ToString();
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
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
