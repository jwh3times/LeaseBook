using System.Net;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Shouldly;

namespace LeaseBook.Tests.Integration.Security;

[Collection(nameof(DatabaseCollection))]
public sealed class AllowedHostsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Configured_allowed_hosts_rejects_a_forged_host_header()
    {
        // Override AllowedHosts to a single hostname; host filtering must then reject anything else.
        var factory = fixture.Api.WithWebHostBuilder(b => b.UseSetting("AllowedHosts", "leasebook.example.com"));
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = "evil.example.com";
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Configured_allowed_hosts_accepts_the_real_host()
    {
        var factory = fixture.Api.WithWebHostBuilder(b => b.UseSetting("AllowedHosts", "leasebook.example.com"));
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = "leasebook.example.com";
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
