using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Seeding;
using Shouldly;

namespace LeaseBook.Tests.Integration.Security;

/// <summary>
/// WP-5 / finding F7: the statement-deliver endpoint (<c>POST /api/statements/{ownerId}/deliver</c>)
/// takes the recipient email as a query-string parameter (<c>?toEmail=</c>), and <c>Program.cs</c>
/// registers <c>AddAspNetCoreInstrumentation()</c> with no explicit query-string scrubbing
/// configured. This test empirically verifies whether the recipient email reaches the telemetry
/// that would be exported to App Insights.
/// <para>
/// <b>Empirical result (verified via a diagnostic dump of every tag on every Activity produced
/// during this request, across all sources — CQRS, Npgsql, and
/// <c>Microsoft.AspNetCore.Hosting.HttpRequestIn</c>):</b> the ASP.NET Core hosting Activity does
/// carry a <c>url.query</c> tag containing the raw query string shape, but
/// <c>OpenTelemetry.Instrumentation.AspNetCore</c> redacts every query-string parameter value by
/// default — the observed tag value is literally
/// <c>?year=Redacted&amp;month=Redacted&amp;basis=Redacted&amp;toEmail=Redacted</c>, never the real
/// email. Disabling that redaction requires the opt-out environment variable
/// <c>OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION=true</c>, which this repo does
/// not set anywhere. No <c>url.full</c>/<c>http.target</c>/<c>http.url</c> tag is emitted under this
/// app's default (new) semantic-convention mode, so there is no alternate tag carrying the
/// unredacted value either.
/// </para>
/// <para>
/// <b>Verdict: F7 is not exploitable through the OpenTelemetry export path as currently
/// configured.</b> This test is kept as a regression guard — it fails if a future change (e.g. an
/// explicit opt-out of redaction, an enrichment hook copying the raw query, or a switch to the
/// legacy <c>http.target</c>/<c>http.url</c> tags) reintroduces the leak. It checks both wire
/// encodings of the recipient email (raw and percent-encoded), since a reintroduced leak could
/// surface either form.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class DeliverTelemetryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Deliver_request_does_not_record_the_recipient_email_in_telemetry()
    {
        var ct = TestContext.Current.CancellationToken;
        const string secretEmail = "recipient-secret@example.com";
        var secretEmailEncoded = Uri.EscapeDataString(secretEmail);
        var captured = new List<string>();

        // Listen on every Activity the ASP.NET Core hosting pipeline produces (the same Activity
        // AddAspNetCoreInstrumentation() enriches with url.*/http.* tags before exporting). Sampling
        // AllData ensures tags are actually recorded on the Activity object, mirroring what the
        // OTel SDK's own listener requests for export.
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                foreach (var tag in activity.TagObjects)
                {
                    // The request is sent with the email percent-encoded on the wire
                    // (Uri.EscapeDataString below), so a leaked tag could carry either the
                    // percent-encoded wire form or a decoded form. Check both — matching only the
                    // decoded form would miss a raw url.query leak entirely (see F7 critical finding).
                    if (tag.Value is string v &&
                        (v.Contains(secretEmail, StringComparison.OrdinalIgnoreCase) ||
                         v.Contains(secretEmailEncoded, StringComparison.OrdinalIgnoreCase)))
                    {
                        captured.Add($"{tag.Key}={v}");
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        // Provision: seeded demo org + PMAdmin login (satisfies the deliver endpoint's
        // RequirePMStaff policy) + O5's May 2026 balanced statement — mirrors
        // StatementDeliveryTests.DemoClientAsync/Deliver_endpoint_balanced_statement_returns_200_with_queued_state.
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(DemoSeeder.AdminEmail, DemoSeeder.AdminPassword), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, "login must succeed to reach the deliver endpoint");
        await client.PrimeCsrfAsync(ct); // XSRF token rotates on sign-in

        var url = $"/api/statements/{DemoIds.O5}/deliver" +
                  $"?year=2026&month=5&basis=cash&toEmail={secretEmailEncoded}";
        var response = await client.PostAsync(url, null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "the deliver call must actually succeed for this test to exercise the real request path: " +
            await response.Content.ReadAsStringAsync(ct));

        captured.ShouldBeEmpty($"Recipient email leaked into telemetry tags: {string.Join("; ", captured)}");
    }
}
