using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #474 over the real HTTP host: a bank account's MICR details (routing number, the bank's On-Us field,
/// stock kind, MICR line offsets). The numbers are write-only — every read shows the last four digits —
/// only an administrator may change them, and each org sees only its own. Each test seeds its own org.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class BankMicrDetailsHttpTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";
    private const string Routing = "111000012";
    private const string OnUs = "123456789U";

    [Fact]
    public async Task An_admin_saves_the_numbers_and_every_read_shows_only_their_last_four_digits()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var staff = await LoggedInClientAsync(setup.StaffEmail, ct);
        var admin = await LoggedInClientAsync(setup.AdminEmail, ct);
        var url = $"/api/refund-checks/micr/{setup.BankId}";

        var initial = await ReadAsync(staff, url, ct);
        initial.ShouldBe(new MicrView(setup.BankId, "preprinted", null, null, 0m, 0m));

        var saved = await admin.PutAsJsonAsync(url, new
        {
            stockKind = "blank",
            routingNumber = Routing,
            onUsAccountNumber = OnUs,
            micrOffsetXPoints = 1.5m,
            micrOffsetYPoints = -2m,
        }, ct);
        var savedBody = await saved.Content.ReadAsStringAsync(ct);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, savedBody);
        savedBody.ShouldNotContain(Routing);
        savedBody.ShouldNotContain("123456789");
        JsonSerializer.Deserialize<MicrView>(savedBody, Json)
            .ShouldBe(new MicrView(setup.BankId, "blank", "0012", "6789", 1.5m, -2m));

        var read = await staff.GetAsync(url, ct);
        var readBody = await read.Content.ReadAsStringAsync(ct);
        readBody.ShouldNotContain(Routing);
        readBody.ShouldNotContain("123456789");
        JsonSerializer.Deserialize<MicrView>(readBody, Json)
            .ShouldBe(new MicrView(setup.BankId, "blank", "0012", "6789", 1.5m, -2m));

        // Omitted numbers are kept: the form never has the saved values to send back.
        var offsetsOnly = await admin.PutAsJsonAsync(url, new
        {
            stockKind = "blank",
            micrOffsetXPoints = 0m,
            micrOffsetYPoints = 0m,
        }, ct);
        offsetsOnly.StatusCode.ShouldBe(HttpStatusCode.OK, await offsetsOnly.Content.ReadAsStringAsync(ct));
        (await ReadAsync(staff, url, ct)).ShouldBe(new MicrView(setup.BankId, "blank", "0012", "6789", 0m, 0m));
    }

    [Fact]
    public async Task Staff_can_read_but_not_change_them_and_another_org_sees_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var staff = await LoggedInClientAsync(setup.StaffEmail, ct);
        var admin = await LoggedInClientAsync(setup.AdminEmail, ct);
        var url = $"/api/refund-checks/micr/{setup.BankId}";
        var body = new { stockKind = "blank", routingNumber = Routing, onUsAccountNumber = OnUs, micrOffsetXPoints = 0m, micrOffsetYPoints = 0m };

        var refused = await staff.PutAsJsonAsync(url, body, ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var refusedBody = await refused.Content.ReadAsStringAsync(ct);
        refusedBody.ShouldNotContain(Routing);
        refusedBody.ShouldNotContain("123456789");
        (await ReadAsync(staff, url, ct)).RoutingNumberLast4.ShouldBeNull();

        (await admin.PutAsJsonAsync(url, body, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var other = await SetupAsync(ct);
        var otherStaff = await LoggedInClientAsync(other.StaffEmail, ct);
        (await ReadAsync(otherStaff, url, ct)).ShouldBe(new MicrView(setup.BankId, "preprinted", null, null, 0m, 0m));
    }

    [Fact]
    public async Task The_numbers_are_ciphertext_at_rest_and_the_audit_trail_records_changes_without_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var admin = await LoggedInClientAsync(setup.AdminEmail, ct);
        var url = $"/api/refund-checks/micr/{setup.BankId}";

        (await admin.PutAsJsonAsync(url, new { stockKind = "blank", routingNumber = Routing, onUsAccountNumber = OnUs, micrOffsetXPoints = 0m, micrOffsetYPoints = 0m }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync(url, new { stockKind = "blank", routingNumber = "540900071", micrOffsetXPoints = 0m, micrOffsetYPoints = 0m }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, setup.OrgId, ct);

        await using (var cmd = new NpgsqlCommand("SELECT routing_number, on_us_account_number FROM bank_micr_profiles", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            (await reader.ReadAsync(ct)).ShouldBeTrue();
            var routing = reader.GetString(0);
            var onUs = reader.GetString(1);
            routing.ShouldNotContain("540900071");
            onUs.ShouldNotContain("123456789");
            routing.Length.ShouldBeGreaterThan(9);
        }

        var payloads = new List<(string Action, string? Before, string? After)>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT action, before::text, after::text FROM audit_events WHERE entity_type = 'bank_micr_profiles' ORDER BY occurred_at, id",
            conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                payloads.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        payloads.Select(p => p.Action).ShouldBe(["insert", "update"]);
        foreach (var (_, before, after) in payloads)
        {
            foreach (var digits in new[] { Routing, "540900071", "123456789" })
            {
                (before ?? "").ShouldNotContain(digits);
                (after ?? "").ShouldNotContain(digits);
            }
        }

        // The update moved the routing number and left the On-Us field alone; the trail says so.
        var update = JsonDocument.Parse(payloads[1].After!).RootElement;
        update.GetProperty("RoutingNumber").GetString().ShouldBe("[redacted] (changed)");
        update.GetProperty("OnUsAccountNumber").GetString().ShouldBe("[redacted]");
        JsonDocument.Parse(payloads[1].Before!).RootElement.GetProperty("RoutingNumber").GetString().ShouldBe("[redacted]");
    }

    [Theory]
    [InlineData("blank", "111000013", "123456789U", 0)]   // routing check digit wrong
    [InlineData("blank", "11100001", "123456789U", 0)]    // eight digits
    [InlineData("blank", "000000000", "123456789U", 0)]   // passes the arithmetic, names no bank
    [InlineData("blank", Routing, "1234A6789", 0)]        // a letter the MICR font cannot print
    [InlineData("blank", Routing, "1234567890123456789", 0)] // 19 positions; the field has 18
    [InlineData("blank", Routing, "12U-", 0)]             // fewer than four digits
    [InlineData("blank", Routing, OnUs, 19)]              // beyond a quarter inch
    [InlineData("blank", null, null, 0)]                  // blank stock with no numbers saved
    [InlineData("embossed", Routing, OnUs, 0)]            // unknown stock kind
    public async Task Invalid_details_are_a_validation_error(string stockKind, string? routing, string? onUs, int offsetX)
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var admin = await LoggedInClientAsync(setup.AdminEmail, ct);

        var response = await admin.PutAsJsonAsync($"/api/refund-checks/micr/{setup.BankId}", new
        {
            stockKind,
            routingNumber = routing,
            onUsAccountNumber = onUs,
            micrOffsetXPoints = (decimal)offsetX,
            micrOffsetYPoints = 0m,
        }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe("validation_failed");

        // A refusal never echoes what was submitted.
        foreach (var submitted in new[] { routing, onUs }.Where(v => v is { Length: >= 4 }))
        {
            body.ShouldNotContain(new string(submitted!.Where(char.IsAsciiDigit).ToArray()));
        }
    }

    [Fact]
    public async Task Blank_stock_without_numbers_is_refused_with_a_reason_the_form_can_show()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var admin = await LoggedInClientAsync(setup.AdminEmail, ct);

        var response = await admin.PutAsJsonAsync($"/api/refund-checks/micr/{setup.BankId}",
            new { stockKind = "blank", micrOffsetXPoints = 0m, micrOffsetYPoints = 0m }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        var errors = JsonDocument.Parse(body).RootElement.GetProperty("errors");
        errors.GetProperty("StockKind")[0].GetString()
            .ShouldBe("Blank check stock needs the routing number and the On-Us field.");
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The wire shape, read independently of the production record.</summary>
    private sealed record MicrView(Guid BankAccountId, string StockKind, string? RoutingNumberLast4,
        string? OnUsAccountNumberLast4, decimal MicrOffsetXPoints, decimal MicrOffsetYPoints);

    private sealed record Setup(Guid OrgId, string StaffEmail, string AdminEmail, Guid BankId);

    private static async Task<MicrView> ReadAsync(HttpClient client, string url, CancellationToken ct)
    {
        var response = await client.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<MicrView>(body, Json)!;
    }

    private async Task<Setup> SetupAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"MICR Details Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var staffEmail = await CreateUserAsync(orgId, "staff", Roles.PMStaff, ct);
        var adminEmail = await CreateUserAsync(orgId, "admin", Roles.PMAdmin, ct);

        Guid bankId = default;
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var s = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsSystemAsync(orgId, "test-harness", async () =>
            {
                bankId = (await s.Send(new CreateBankAccount("Deposit Trust", null, null, "deposit"), ct)).Id;
            }, ct);
        }

        return new Setup(orgId, staffEmail, adminEmail, bankId);
    }

    private async Task<string> CreateUserAsync(Guid orgId, string name, string role, CancellationToken ct)
    {
        var email = $"{name}-{orgId:N}@example.com";
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            Id = UuidV7.NewId(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            OrgId = orgId,
            DisplayName = name,
        };
        (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
        (await userManager.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
        return email;
    }

    private async Task<HttpClient> LoggedInClientAsync(string email, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync(ct));
        await client.PrimeCsrfAsync(ct);
        return client;
    }
}
