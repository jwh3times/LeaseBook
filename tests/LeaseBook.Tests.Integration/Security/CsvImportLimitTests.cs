using System.Net;
using System.Net.Http.Json;
using LeaseBook.SharedKernel.Csv;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration.Security;

/// <summary>
/// Every CSV import takes the file as a JSON string and parses it in memory, so each carries an
/// explicit bound: a character limit its validation enforces with a message an operator can act on,
/// and a request-body limit on the endpoint so an oversized upload is refused before it is read.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CsvImportLimitTests(PostgresFixture fixture)
{
    public static TheoryData<string> ImportRoutes() =>
    [
        "/api/banking/banks/{0}/imports",
        "/api/onboarding/import/owners",
        "/api/onboarding/import-balances/bank_balances",
        "/api/onboarding/import-balances/bank_balances/supersede",
    ];

    [Theory]
    [MemberData(nameof(ImportRoutes))]
    public async Task A_file_over_the_limit_is_refused_with_the_limit_named(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, client) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMAdmin, ct);

        // Otherwise valid in every respect the endpoint checks first — a real kind, a valid cutover
        // date, a complete column map — so the size is the only thing that can refuse it.
        var oversized = new string('x', CsvImportLimits.MaxCharacters + 1);
        var body = new
        {
            filename = "big.csv",
            csvContent = oversized,
            cutoverDate = "2026-06-30",
            columnMap = new { date = "Date", description = "Description", amount = "Amount" },
        };

        var response = await client.PostAsJsonAsync(string.Format(route, Guid.NewGuid()), body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain(CsvImportLimits.TooLargeMessage);
    }

    /// <summary>
    /// The test server does not enforce request-body limits, so this pins the endpoint metadata that
    /// Kestrel does enforce: it is what refuses an oversized upload before it is buffered.
    /// </summary>
    [Fact]
    public void Every_csv_import_endpoint_carries_the_request_body_limit()
    {
        var endpoints = fixture.Api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw
                && (raw.EndsWith("/imports", StringComparison.Ordinal)
                    || raw.StartsWith("/api/onboarding/import", StringComparison.Ordinal)))
            .ToList();

        endpoints.Count.ShouldBe(4, string.Join(", ", endpoints.Select(e => e.RoutePattern.RawText)));
        foreach (var endpoint in endpoints)
        {
            var limit = endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
            limit.ShouldNotBeNull($"{endpoint.RoutePattern.RawText} has no request-body limit");
            (limit.MaxRequestBodySize == CsvImportLimits.MaxRequestBytes).ShouldBeTrue(
                $"{endpoint.RoutePattern.RawText}: {limit.MaxRequestBodySize}");
        }
    }
}
