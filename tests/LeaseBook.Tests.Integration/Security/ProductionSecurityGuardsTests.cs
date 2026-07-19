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

    [Fact]
    public void Non_development_with_real_host_but_no_durable_keyring_throws()
    {
        var config = BuildConfig("leasebook.example.com", durable: null);

        var ex = Should.Throw<InvalidOperationException>(
            () => ProductionSecurityGuards.Validate(config, new StubEnvironment("Production")));
        ex.Message.ShouldContain("DataProtection:Durable");
        ex.Message.ShouldContain("F8");
    }

    [Fact]
    public void Non_development_with_durable_keyring_explicitly_false_throws()
    {
        var config = BuildConfig("leasebook.example.com", durable: false);

        var ex = Should.Throw<InvalidOperationException>(
            () => ProductionSecurityGuards.Validate(config, new StubEnvironment("Production")));
        ex.Message.ShouldContain("DataProtection:Durable");
    }

    [Fact]
    public void Non_development_with_real_host_and_durable_keyring_does_not_throw()
    {
        var config = BuildConfig("leasebook.example.com", durable: true);

        Should.NotThrow(() => ProductionSecurityGuards.Validate(config, new StubEnvironment("Production")));
    }

    private static IConfiguration BuildConfig(string? allowedHosts, bool? durable)
    {
        var values = new Dictionary<string, string?>();
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
