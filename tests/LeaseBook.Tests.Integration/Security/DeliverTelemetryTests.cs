using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Directory.Endpoints;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Seeding;
using Shouldly;

namespace LeaseBook.Tests.Integration.Security;

/// <summary>
/// The statement-deliver endpoint (<c>POST /api/statements/{ownerId}/deliver</c>) sends to the
/// owner's address on file, so the recipient never travels in the request: it is read from the
/// owner record server-side. This guards the other half — that the resolved address is not copied
/// into any telemetry the request produces. The listener takes every <see cref="ActivitySource"/>
/// (hosting, the CQRS source, Npgsql), because the address now flows through a Directory query
/// rather than the URL.
/// <para>
/// Earlier the address rode in the query string, and this test established that
/// <c>OpenTelemetry.Instrumentation.AspNetCore</c> redacts query values by default. That no longer
/// matters to this endpoint, and a supplied <c>toEmail</c> is still sent here to prove it is
/// ignored rather than echoed. Both wire encodings are checked, since a leak could surface either.
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
            ShouldListenTo = _ => true,
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

        // A fresh org, so the owner this creates cannot move the demo org's golden counts.
        var (_, client) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMAdmin, ct);

        var created = await client.PostAsJsonAsync("/api/directory/owners",
            new CreateOwner($"Telemetry owner {Guid.NewGuid():N}", null, secretEmail, null, null, 0m), ct);
        created.StatusCode.ShouldBe(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(ct));
        var ownerId = (await created.Content.ReadFromJsonAsync<CreatedId>(ct))!.Id;
        captured.Clear(); // only the delivery is under test, not the create that put the address on file

        var url = $"/api/statements/{ownerId}/deliver" +
                  $"?year=2026&month=5&basis=cash&toEmail={secretEmailEncoded}";
        var response = await client.PostAsync(url, null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "the deliver call must actually succeed for this test to exercise the real request path: " +
            await response.Content.ReadAsStringAsync(ct));

        captured.ShouldBeEmpty($"Recipient email leaked into telemetry tags: {string.Join("; ", captured)}");
    }
}
