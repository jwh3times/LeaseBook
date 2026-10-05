using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>The per-method convenience-fee rules in organization settings (ADR-053, #500).</summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PaymentFeeSettingsTests(PostgresFixture fixture)
{
    private const string Path = "/api/settings/payment-fees";

    private static object Rules(int cardBps = 290, decimal cardFixed = 0.30m, decimal? cardCap = null,
        int achBps = 80, decimal achFixed = 0m, decimal? achCap = 5m) => new
        {
            cardFeeRateBps = cardBps,
            cardFeeFixed = cardFixed,
            cardFeeCap = cardCap,
            achFeeRateBps = achBps,
            achFeeFixed = achFixed,
            achFeeCap = achCap,
        };

    [Fact]
    public async Task Fee_rules_default_to_charging_nothing_and_round_trip_through_an_admin_update()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, admin) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMAdmin, ct);
        using var client = admin;
        var defaults = await client.GetFromJsonAsync<JsonElement>("/api/settings/org", ct);
        Read(defaults).ShouldBe((0, 0m, null, 0, 0m, null));

        var response = await client.PutAsJsonAsync(Path, Rules(), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        Read(await response.Content.ReadFromJsonAsync<JsonElement>(ct)).ShouldBe((290, 0.30m, null, 80, 0m, 5m));

        // An unrelated settings update leaves the rules alone, and the rules update left it alone.
        (await client.PutAsJsonAsync("/api/settings/org", new { legalName = "Fee test company", lateFeeAmount = 75m }, ct))
            .EnsureSuccessStatusCode();
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings/org", ct);
        Read(settings).ShouldBe((290, 0.30m, null, 80, 0m, 5m));
        settings.GetProperty("legalName").GetString().ShouldBe("Fee test company");

        // The change is on the audit trail under the administrator who made it.
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(orgId, "test-harness", async () =>
        {
            var audits = await db.AuditEvents.Where(a => a.EntityType == "org_settings")
                .OrderBy(a => a.OccurredAt).ToListAsync(ct);
            // The first row whose state carries the new rate is the rules update itself.
            var change = audits.First(a => a.After != null && a.After.Contains("290"));
            (change.ActorKind, change.ActorUserId.HasValue).ShouldBe(("user", true));
        }, ct);
    }

    [Fact]
    public async Task Staff_can_read_but_not_change_the_rules_and_another_organization_keeps_its_own()
    {
        var ct = TestContext.Current.CancellationToken;
        var (orgId, admin) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMAdmin, ct);
        using var adminClient = admin;
        (await adminClient.PutAsJsonAsync(Path, Rules(), ct)).EnsureSuccessStatusCode();
        var email = $"fee-staff-{orgId:N}@example.test";
        await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Staff", Roles.PMStaff, ct);
        using var staff = fixture.Api.CreateClient();
        await staff.PrimeCsrfAsync(ct);
        await AuthTestSupport.LoginAsync(staff, email, ct);
        await staff.PrimeCsrfAsync(ct);

        Read(await staff.GetFromJsonAsync<JsonElement>("/api/settings/org", ct)).ShouldBe((290, 0.30m, null, 80, 0m, 5m));
        (await staff.PutAsJsonAsync(Path, Rules(cardBps: 0), ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        Read(await staff.GetFromJsonAsync<JsonElement>("/api/settings/org", ct)).Item1.ShouldBe(290);

        var (_, other) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMAdmin, ct);
        using var otherClient = other;
        Read(await otherClient.GetFromJsonAsync<JsonElement>("/api/settings/org", ct)).ShouldBe((0, 0m, null, 0, 0m, null));
    }

    public static TheoryData<string, string, string> Refused() => new()
    {
        { "a rate above the limit", "CardFeeRateBps", """{"cardFeeRateBps":2001,"cardFeeFixed":0.30,"achFeeRateBps":80,"achFeeFixed":0}""" },
        { "a negative rate", "AchFeeRateBps", """{"cardFeeRateBps":290,"cardFeeFixed":0.30,"achFeeRateBps":-1,"achFeeFixed":0}""" },
        { "a negative fixed fee", "CardFeeFixed", """{"cardFeeRateBps":290,"cardFeeFixed":-0.01,"achFeeRateBps":80,"achFeeFixed":0}""" },
        { "a fixed fee in fractions of a cent", "CardFeeFixed", """{"cardFeeRateBps":290,"cardFeeFixed":0.305,"achFeeRateBps":80,"achFeeFixed":0}""" },
        { "a cap of zero", "AchFeeCap", """{"cardFeeRateBps":290,"cardFeeFixed":0.30,"achFeeRateBps":80,"achFeeFixed":0,"achFeeCap":0}""" },
        { "a cap below the fixed fee", "CardFeeCap", """{"cardFeeRateBps":290,"cardFeeFixed":0.30,"cardFeeCap":0.29,"achFeeRateBps":80,"achFeeFixed":0}""" },
        { "a missing rate", "CardFeeRateBps", """{"cardFeeFixed":0.30,"achFeeRateBps":80,"achFeeFixed":0}""" },
        { "a missing fixed fee", "AchFeeFixed", """{"cardFeeRateBps":290,"cardFeeFixed":0.30,"achFeeRateBps":80}""" },
        { "an unknown field", "", """{"cardFeeRateBps":290,"cardFeeFixed":0.30,"achFeeRateBps":80,"achFeeFixed":0,"wireFeeFixed":1}""" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_rule_that_is_out_of_range_or_incomplete_is_refused_and_changes_nothing(string what, string field, string body)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, admin) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMAdmin, ct);
        using var client = admin;
        (await client.PutAsJsonAsync(Path, Rules(), ct)).EnsureSuccessStatusCode();

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PutAsync(Path, content, ct);

        var problem = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{what}: {problem}");
        // A validation failure names the field it is about, so the form can show it beside that field.
        if (field.Length > 0) { problem.ShouldContain(field, Case.Insensitive); }
        Read(await client.GetFromJsonAsync<JsonElement>("/api/settings/org", ct)).ShouldBe((290, 0.30m, null, 80, 0m, 5m));
    }

    private static (int, decimal, decimal?, int, decimal, decimal?) Read(JsonElement settings) => (
        settings.GetProperty("cardFeeRateBps").GetInt32(),
        settings.GetProperty("cardFeeFixed").GetDecimal(),
        Cap(settings.GetProperty("cardFeeCap")),
        settings.GetProperty("achFeeRateBps").GetInt32(),
        settings.GetProperty("achFeeFixed").GetDecimal(),
        Cap(settings.GetProperty("achFeeCap")));

    private static decimal? Cap(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetDecimal();
}
