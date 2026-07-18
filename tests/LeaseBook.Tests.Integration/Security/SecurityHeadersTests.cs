using System.Net.Http.Json;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using Shouldly;

namespace LeaseBook.Tests.Integration.Security;

[Collection(nameof(DatabaseCollection))]
public sealed class SecurityHeadersTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("/api/health")]
    [InlineData("/")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        var client = fixture.Api.CreateClient();
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        response.Headers.GetValues("X-Frame-Options").ShouldContain("DENY");
        response.Headers.GetValues("Referrer-Policy").ShouldContain("no-referrer");
        response.Headers.Contains("Content-Security-Policy").ShouldBeTrue();
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("frame-ancestors 'none'");
        response.Headers.Contains("Permissions-Policy").ShouldBeTrue();
    }
}
