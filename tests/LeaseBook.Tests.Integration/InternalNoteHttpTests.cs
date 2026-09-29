using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #468 over HTTP: the staff write surface takes an owner-facing <c>description</c> (formerly
/// <c>memo</c>) and a staff-only <c>internalNote</c>; a void's reason becomes the reversal's note; and
/// every staff read surface shows the note beside the description — tenant ledger (JSON and CSV), bank
/// register (and its search), the trust-ledger report, and the staff statement view's note overlay.
/// Reads raw JSON on purpose: the property names are the contract the SPA builds against.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class InternalNoteHttpTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";
    private static readonly DateOnly Feb1 = new(2026, 2, 1);
    private static readonly DateOnly Feb3 = new(2026, 2, 3);
    private static readonly DateOnly Feb10 = new(2026, 2, 10);

    [Fact]
    public async Task Every_staff_write_takes_a_description_and_an_internal_note()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        using var client = await LoggedInClientAsync(setup, ct);
        var tenant = $"/api/accounting/tenants/{setup.TenantId}";

        var posted = new List<(Guid Id, string Description, string Note)>
        {
            (await PostAsync(client, $"{tenant}/charges", new
            {
                amount = 1000m, date = Feb1, kind = "rent",
                description = "February rent", internalNote = "note:charge", sourceRef = Key(),
            }, ct), "February rent", "note:charge"),
            (await PostAsync(client, $"{tenant}/credits", new
            {
                amount = 10m, date = Feb1, reason = "Goodwill credit",
                internalNote = "note:credit", sourceRef = Key(),
            }, ct), "Goodwill credit", "note:credit"),
            (await PostAsync(client, $"{tenant}/payments", new
            {
                amount = 500m, date = Feb3, method = "ach", bankAccountId = setup.TrustBankId,
                description = "Check 1042", internalNote = "note:payment", sourceRef = Key(),
            }, ct), "Check 1042", "note:payment"),
            (await PostAsync(client, $"{tenant}/prepayments", new
            {
                amount = 100m, date = Feb3, bankAccountId = setup.TrustBankId,
                description = "March prepayment", internalNote = "note:prepayment", sourceRef = Key(),
            }, ct), "March prepayment", "note:prepayment"),
            (await PostAsync(client, $"{tenant}/prepayment-applications", new
            {
                amount = 100m, date = Feb3, bankAccountId = setup.TrustBankId,
                description = "Prepayment applied", internalNote = "note:prepayment-applied", sourceRef = Key(),
            }, ct), "Prepayment applied", "note:prepayment-applied"),
            (await PostAsync(client, $"{tenant}/deposits", new
            {
                amount = 800m, date = Feb1, depositBankId = setup.DepositBankId,
                description = "Security deposit", internalNote = "note:deposit", sourceRef = Key(),
            }, ct), "Security deposit", "note:deposit"),
            (await PostAsync(client, $"{tenant}/deposit-applications", new
            {
                amount = 100m, date = Feb3, depositBankId = setup.DepositBankId, operatingBankId = setup.TrustBankId,
                target = "to-owner-income", reason = "Carpet damage", internalNote = "note:deposit-applied",
                sourceRef = Key(),
            }, ct), "Carpet damage", "note:deposit-applied"),
            (await PostAsync(client, $"/api/accounting/banks/{setup.TrustBankId}/adjustments", new
            {
                kind = "interest", amount = 3m, date = Feb3,
                description = "February interest", internalNote = "note:interest", sourceRef = Key(),
            }, ct), "February interest", "note:interest"),
        };

        var stored = await ReadEntriesAsync(setup.OrgId, posted.Select(p => p.Id).ToList(), ct);
        foreach (var (id, description, note) in posted)
        {
            stored[id].Description.ShouldBe(description);
            stored[id].InternalNote.ShouldBe(note);
        }

        // The audit row captures it through SaveChanges, with no call-site wiring.
        using (var audited = JsonDocument.Parse(await AuditAfterAsync(setup.OrgId, posted[0].Id, ct)))
        {
            audited.RootElement.GetProperty("InternalNote").GetString().ShouldBe("note:charge");
        }

        // The note is optional everywhere: omitting it posts with no note, not an empty one.
        var bare = await PostAsync(client, $"{tenant}/charges",
            new { amount = 5m, date = Feb1, kind = "other", description = "Key copy", sourceRef = Key() }, ct);
        (await ReadEntriesAsync(setup.OrgId, [bare], ct))[bare].InternalNote.ShouldBeNull();
    }

    [Fact]
    public async Task The_tenant_ledger_shows_the_note_beside_the_description_including_a_voids_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        using var client = await LoggedInClientAsync(setup, ct);
        var tenant = $"/api/accounting/tenants/{setup.TenantId}";

        var charge = await PostAsync(client, $"{tenant}/charges", new
        {
            amount = 1000m,
            date = Feb1,
            kind = "rent",
            description = "February rent",
            internalNote = "Lease addendum pending",
            sourceRef = Key(),
        }, ct);
        var reversal = await PostAsync(client, $"/api/accounting/entries/{charge}/void",
            new { reason = "Keyed against the wrong unit", sourceRef = Key() }, ct);

        var ledger = await GetJsonAsync(client, $"{tenant}/ledger", ct);
        var rows = ledger["rows"]!.AsArray();
        var original = rows.Single(r => (Guid)r!["entryId"]! == charge)!;
        original["description"]!.GetValue<string>().ShouldBe("February rent");
        original["internalNote"]!.GetValue<string>().ShouldBe("Lease addendum pending");
        var voidRow = rows.Single(r => (Guid)r!["entryId"]! == reversal)!;
        voidRow["description"]!.GetValue<string>().ShouldBe("Void — February rent");
        voidRow["internalNote"]!.GetValue<string>().ShouldBe("Keyed against the wrong unit");

        var csv = await client.GetStringAsync($"{tenant}/ledger.csv", ct);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldBe("Date,Category,Description,Internal note,Charge,Payment,Balance,Status");
        lines.ShouldContain("2026-02-01,Rent,February rent,Lease addendum pending,1000.00,0.00,1000.00,Voided");
        lines.ShouldContain(l => l.Contains(",Void — February rent,Keyed against the wrong unit,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_bank_register_and_trust_ledger_show_the_note_and_search_finds_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        using var client = await LoggedInClientAsync(setup, ct);

        var payment = await PostAsync(client, $"/api/accounting/tenants/{setup.TenantId}/payments", new
        {
            amount = 500m,
            date = Feb3,
            method = "check",
            bankAccountId = setup.TrustBankId,
            description = "Check 1042",
            internalNote = "Paid by guarantor Wexley",
            sourceRef = Key(),
        }, ct);
        await PostAsync(client, $"/api/accounting/tenants/{setup.TenantId}/payments", new
        {
            amount = 25m,
            date = Feb3,
            method = "cash",
            bankAccountId = setup.TrustBankId,
            description = "Cash drop",
            sourceRef = Key(),
        }, ct);

        var register = $"/api/accounting/banks/{setup.TrustBankId}/register";
        var all = (await GetJsonAsync(client, register, ct))["rows"]!.AsArray();
        all.Count.ShouldBe(2);
        var row = all.Single(r => r!["description"]!.GetValue<string>() == "Check 1042")!;
        row["internalNote"]!.GetValue<string>().ShouldBe("Paid by guarantor Wexley");
        all.Single(r => r!["description"]!.GetValue<string>() == "Cash drop")!["internalNote"].ShouldBeNull();

        // The search reads the note as well as the description.
        var byNote = (await GetJsonAsync(client, $"{register}?search=wexley", ct))["rows"]!.AsArray();
        byNote.Select(r => r!["description"]!.GetValue<string>()).ShouldBe(["Check 1042"]);
        var byDescription = (await GetJsonAsync(client, $"{register}?search=drop", ct))["rows"]!.AsArray();
        byDescription.Select(r => r!["description"]!.GetValue<string>()).ShouldBe(["Cash drop"]);

        // The trust-ledger report is the same register read, with the note as its own column.
        var preview = await GetJsonAsync(client, $"/api/reports/trust-ledger/preview?bankAccountId={setup.TrustBankId}", ct);
        preview["columns"]!.AsArray().Select(c => c!.GetValue<string>())
            .ShouldBe(["journalLineId", "date", "description", "internalNote", "deposit", "withdrawal", "status"]);
        preview["rows"]!.AsArray()
            .Single(r => r!["description"]!.GetValue<string>() == "Check 1042")!["internalNote"]!
            .GetValue<string>().ShouldBe("Paid by guarantor Wexley");
        var reportCsv = await client.GetStringAsync($"/api/reports/trust-ledger/csv?bankAccountId={setup.TrustBankId}", ct);
        reportCsv.ShouldContain("Paid by guarantor Wexley");
        payment.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task The_staff_statement_view_reads_notes_through_its_own_overlay_not_the_owner_statement()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        using var client = await LoggedInClientAsync(setup, ct);

        var charge = await PostAsync(client, $"/api/accounting/tenants/{setup.TenantId}/charges", new
        {
            amount = 1000m,
            date = Feb1,
            kind = "rent",
            description = "February rent",
            internalNote = "Owner agreed to a rent review",
            sourceRef = Key(),
        }, ct);
        var quiet = await PostAsync(client, $"/api/accounting/tenants/{setup.TenantId}/charges", new
        {
            amount = 40m,
            date = Feb3,
            kind = "other",
            description = "Key copy",
            sourceRef = Key(),
        }, ct);
        var reversal = await PostAsync(client, $"/api/accounting/entries/{charge}/void",
            new { reason = "Rent review reduced the figure", asOfDate = Feb10, sourceRef = Key() }, ct);

        const string Query = "?year=2026&month=2&basis=accrual";

        // The owner's statement (what is rendered, issued and served) names the void, never the reason.
        var statementJson = await client.GetStringAsync($"/api/statements/{setup.OwnerId}{Query}", ct);
        statementJson.ShouldContain("Void — February rent");
        statementJson.ShouldNotContain("Rent review reduced", Case.Insensitive);
        statementJson.ShouldNotContain("rent review", Case.Insensitive);
        statementJson.ShouldNotContain("internalNote", Case.Insensitive);

        // The staff overlay: one note per statement entry that has one, keyed by entry id.
        var overlay = await GetJsonAsync(client, $"/api/statements/{setup.OwnerId}/internal-notes{Query}", ct);
        var notes = overlay["notes"]!.AsArray()
            .ToDictionary(n => (Guid)n!["entryId"]!, n => n!["internalNote"]!.GetValue<string>());
        notes.ShouldBe(new Dictionary<Guid, string>
        {
            [charge] = "Owner agreed to a rent review",
            [reversal] = "Rent review reduced the figure",
        }, ignoreOrder: true);
        notes.ShouldNotContainKey(quiet);
    }

    private sealed record Setup(Guid OrgId, string Email, Guid OwnerId, Guid TenantId, Guid TrustBankId, Guid DepositBankId);

    private static string Key() => UuidV7.NewId().ToString();

    private async Task<Dictionary<Guid, JournalEntry>> ReadEntriesAsync(
        Guid orgId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Dictionary<Guid, JournalEntry> entries = [];
        await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(orgId, "test-harness", async () =>
            entries = await db.Set<JournalEntry>().AsNoTracking()
                .Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct), ct);
        return entries;
    }

    private async Task<string> AuditAfterAsync(Guid orgId, Guid entryId, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = string.Empty;
        await scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>().RunAsSystemAsync(orgId, "test-harness", async () =>
            after = await db.AuditEvents.AsNoTracking()
                .Where(a => a.EntityType == "journal_entries" && a.EntityId == entryId && a.Action == "insert")
                .Select(a => a.After!).SingleAsync(ct), ct);
        return after;
    }

    private async Task<Setup> SetupAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Internal Note Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"staff-{orgId:N}@example.com";
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser
            {
                Id = UuidV7.NewId(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                OrgId = orgId,
                DisplayName = "Renée Calloway",
            };
            (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
        }

        Guid ownerId = default, tenantId = default, trustBankId = default, depositBankId = default;
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsSystemAsync(orgId, "test-harness", async () =>
            {
                ownerId = await sender.Send(new CreateOwner("Owner", null, null, null, 800, 0m), ct);
                var propertyId = await sender.Send(new CreateProperty(ownerId, "412 Oakmont Ave", "Asheville", "NC", "28801", null), ct);
                var unitId = await sender.Send(new CreateUnit(propertyId, "#2B", 1450m, "available"), ct);
                tenantId = await sender.Send(new CreateTenant("Jasmine Carter", null, null, "current"), ct);
                await sender.Send(new CreateLease(tenantId, unitId, new DateOnly(2025, 6, 1), new DateOnly(2026, 5, 31), 1450m, 1450m, "active"), ct);
                trustBankId = (await sender.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct)).Id;
                depositBankId = (await sender.Send(new CreateBankAccount("Deposit Trust", null, null, "deposit"), ct)).Id;
            }, ct);
        }

        return new Setup(orgId, email, ownerId, tenantId, trustBankId, depositBankId);
    }

    private async Task<HttpClient> LoggedInClientAsync(Setup setup, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(setup.Email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.PrimeCsrfAsync(ct); // XSRF token rotates on sign-in
        return client;
    }

    private static async Task<Guid> PostAsync(HttpClient client, string url, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(url, body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{url}: {await response.Content.ReadAsStringAsync(ct)}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("entryId").GetGuid();
    }

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        var response = await client.GetAsync(url, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{url}: {await response.Content.ReadAsStringAsync(ct)}");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
    }
}
