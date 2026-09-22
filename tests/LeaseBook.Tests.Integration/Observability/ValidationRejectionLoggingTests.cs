using System.Net;
using System.Net.Http.Json;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace LeaseBook.Tests.Integration.Observability;

/// <summary>
/// Every validation 400 logs <see cref="LogEvents.ValidationRejection"/>, whichever path produced it:
/// the CQRS decorator (via <c>ValidationExceptionHandler</c>) or <c>ValidationEndpointFilter</c> on
/// the endpoints that validate outside the dispatcher (auth, telemetry). The diagnostics runbook is
/// keyed on the event id, so an operator searching for it must see both. Only the field count is
/// logged — never a submitted value.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ValidationRejectionLoggingTests(PostgresFixture fixture)
{
    [Fact]
    public async Task An_endpoint_filter_rejection_logs_the_event_without_the_submitted_values()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = fixture.Api.WithWebHostBuilder(_ => { });
        using var logs = new CapturingLoggerProvider();
        host.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);

        var client = host.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("not-an-address", ""), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var rejection = logs.Entries.Where(e => e.EventId.Id == LogEvents.ValidationRejection.Id).ToList();
        rejection.ShouldHaveSingleItem();
        rejection[0].Level.ShouldBe(LogLevel.Warning);
        rejection[0].Message.ShouldNotContain("not-an-address");
    }

    [Fact]
    public async Task A_cqrs_rejection_logs_the_same_event()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = fixture.Api.WithWebHostBuilder(_ => { });
        using var logs = new CapturingLoggerProvider();
        host.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);

        var orgId = UuidV7.NewId();
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, $"Validation Logging Org {orgId:N}", ct);
        var email = $"validation-{orgId:N}@example.com";
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Staff", Roles.PMStaff, ct);
        var client = host.CreateClient();
        await client.PrimeCsrfAsync(ct);
        (await AuthTestSupport.LoginAsync(client, email, ct)).Status.ShouldBe(LoginStatus.Ok);
        await client.PrimeCsrfAsync(ct);

        // A negative charge fails AddChargeValidator inside the CQRS decorator.
        var response = await client.PostAsJsonAsync(
            $"/api/accounting/tenants/{Guid.NewGuid()}/charges",
            new { amount = -5m, date = new DateOnly(2026, 2, 1), kind = "rent", sourceRef = UuidV7.NewId().ToString() },
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        logs.Entries.ShouldContain(e => e.EventId.Id == LogEvents.ValidationRejection.Id && e.Level == LogLevel.Warning);
    }
}
