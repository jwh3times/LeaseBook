using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-03 (M4): the bank register + adjustment + clearance endpoints end-to-end over HTTP against the
/// seeded host. Posts an adjustment, sees it land in the register as uncleared, clears it, and proves a
/// malformed adjustment maps to 400. Runs in its own seeded org so the demo org stays byte-stable.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class BankRegisterHttpTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";
    private static readonly DateOnly Feb1 = new(2026, 2, 1);

    [Fact]
    public async Task Adjustment_appears_in_the_register_and_can_be_cleared()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        // Interest is a deposit-side bank line (bank ↑).
        await PostOkAsync<PostResult>(client, $"/api/accounting/banks/{setup.TrustBankId}/adjustments",
            new { kind = "interest", amount = 100m, date = Feb1, sourceRef = Key() }, ct);

        var register = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        register.Rows.Count.ShouldBe(1);
        var row = register.Rows[0];
        row.Deposit.ShouldBe(100m);
        row.Status.ShouldBe(BankLineStatus.Uncleared);
        register.Totals.Book.ShouldBe(100m);
        register.Totals.UnclearedCount.ShouldBe(1);

        // Clear it through the clearance endpoint.
        var cleared = await PostOkAsync<ClearancesResult>(client, "/api/accounting/banks/clearances",
            new { journalLineIds = new[] { row.JournalLineId } }, ct);
        cleared.Affected.ShouldBe(1);

        var after = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        after.Rows[0].Status.ShouldBe(BankLineStatus.Cleared);
        after.Totals.Cleared.ShouldBe(100m);
        after.Totals.UnclearedCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_transfer_without_a_destination_is_rejected_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        var bad = await client.PostAsJsonAsync($"/api/accounting/banks/{setup.TrustBankId}/adjustments",
            new { kind = "transfer", amount = 50m, date = Feb1, sourceRef = Key() }, ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The refusals a direct API call meets, not only the screen (#553): a fee past the PM's held fees,
    /// and a transfer with the PM operating bank at either end, each with its own stable code.
    /// </summary>
    [Fact]
    public async Task A_fee_past_the_held_fees_and_a_transfer_with_an_operating_bank_are_refused_with_their_codes()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);
        var url = $"/api/accounting/banks/{setup.TrustBankId}/adjustments";
        await PostOkAsync<PostResult>(client, url,
            new { kind = "interest", amount = 40m, date = Feb1, sourceRef = Key() }, ct);

        var fee = await client.PostAsJsonAsync(url,
            new { kind = "fee", amount = 40.01m, date = Feb1, sourceRef = Key() }, ct);
        await ShouldBeProblemAsync(fee, HttpStatusCode.Conflict, "pm_fees_insufficient",
            "This account holds only 40.00 in management fees on or after the fee's date, less than the 40.01 fee, so nothing was posted. A PM contribution to this account dated on or before the fee covers it.", ct);

        var toOperating = await client.PostAsJsonAsync(url,
            new { kind = "transfer", amount = 10m, date = Feb1, toBankAccountId = setup.OperatingBankId, sourceRef = Key() },
            ct);
        await ShouldBeProblemAsync(toOperating, HttpStatusCode.UnprocessableEntity, "transfer_bank_not_trust",
            "A transfer moves money between trust accounts, and the account it goes to is not a trust account.", ct);

        var fromOperating = await client.PostAsJsonAsync(
            $"/api/accounting/banks/{setup.OperatingBankId}/adjustments",
            new { kind = "transfer", amount = 10m, date = Feb1, toBankAccountId = setup.TrustBankId, sourceRef = Key() }, ct);
        await ShouldBeProblemAsync(fromOperating, HttpStatusCode.UnprocessableEntity, "transfer_bank_not_trust",
            "A transfer moves money between trust accounts, and the account it comes from is not a trust account.", ct);

        var register = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        register.Totals.Book.ShouldBe(40m); // only the interest posted
    }

    /// <summary>
    /// A PM contribution over HTTP (#720): it lands in the register as an uncleared deposit and covers a
    /// fee dated after it; into the PM operating bank it is refused with its own code; and once a fee has
    /// drawn on it, voiding it is refused with the held-fees code, naming the bank.
    /// </summary>
    [Fact]
    public async Task A_pm_contribution_covers_a_fee_and_cannot_be_voided_out_from_under_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);
        var url = $"/api/accounting/banks/{setup.TrustBankId}/adjustments";

        var contribution = await PostOkAsync<PostResult>(client, url,
            new { kind = "contribution", amount = 25m, date = Feb1, sourceRef = Key(), internalNote = "covers the Feb fee" }, ct);
        var register = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        var row = register.Rows.ShouldHaveSingleItem();
        (row.Deposit, row.Status, row.Description).ShouldBe((25m, BankLineStatus.Uncleared, "PM contribution"));

        await PostOkAsync<PostResult>(client, url, new { kind = "fee", amount = 25m, date = Feb1, sourceRef = Key() }, ct);

        var toOperating = await client.PostAsJsonAsync($"/api/accounting/banks/{setup.OperatingBankId}/adjustments",
            new { kind = "contribution", amount = 25m, date = Feb1, sourceRef = Key() }, ct);
        await ShouldBeProblemAsync(toOperating, HttpStatusCode.UnprocessableEntity, "contribution_bank_not_trust",
            "A PM contribution is paid into a trust account, and this account is not a trust account.", ct);

        var asOf = new DateOnly(2026, 2, 2);
        var voided = await client.PostAsJsonAsync($"/api/accounting/entries/{contribution.EntryId}/void",
            new { reason = "posted in error", asOfDate = asOf, sourceRef = Key() }, ct);
        await ShouldBeProblemAsync(voided, HttpStatusCode.Conflict, "pm_fees_insufficient",
            "Voiding this entry would take the management fees held in Operating Trust 25.00 below zero on or after "
            + "2026-02-02, so nothing was posted. Void the later charge that uses them first, or record a PM "
            + "contribution to Operating Trust.", ct);

        var after = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        (after.Rows.Count, after.Totals.Book).ShouldBe((2, 0m));
    }

    /// <summary>
    /// A fee sweep over HTTP (#743): the held-fees read gives the most a sweep dated then may take, a sweep
    /// of exactly that posts to both banks, and a sweep past it, into a trust account or dated after today
    /// is refused with its own code.
    /// </summary>
    [Fact]
    public async Task A_sweep_takes_up_to_the_held_fees_floor_into_the_pm_operating_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);
        var url = $"/api/accounting/banks/{setup.TrustBankId}/adjustments";
        var feb10 = new DateOnly(2026, 2, 10);
        // 60.00 held from Feb 1, 40.00 of it gone on Feb 10, 100.00 more earned on Feb 20: 120.00 today,
        // but a sweep dated Feb 1 may take only the 20.00 held on Feb 10.
        await PostOkAsync<PostResult>(client, url, new { kind = "interest", amount = 60m, date = Feb1, sourceRef = Key() }, ct);
        await PostOkAsync<PostResult>(client, url, new { kind = "fee", amount = 40m, date = feb10, sourceRef = Key() }, ct);
        await PostOkAsync<PostResult>(client, url,
            new { kind = "interest", amount = 100m, date = new DateOnly(2026, 2, 20), sourceRef = Key() }, ct);

        var held = await GetAsync<HeldFeesView>(
            client, $"/api/accounting/banks/{setup.TrustBankId}/held-fees?from=2026-02-01", ct);
        (held.Held, held.Floor).ShouldBe((120m, 20m));

        var over = await client.PostAsJsonAsync(url, new
        {
            kind = "sweep",
            amount = 20.01m,
            date = Feb1,
            toBankAccountId = setup.OperatingBankId,
            sourceRef = Key(),
        }, ct);
        await ShouldBeProblemAsync(over, HttpStatusCode.Conflict, "pm_fees_insufficient",
            "This account holds only 20.00 in management fees on or after the sweep's date, less than the 20.01 sweep, so nothing was posted. Sweep no more than the fees held here from that date on.", ct);

        var intoTrust = await client.PostAsJsonAsync($"/api/accounting/banks/{setup.TrustBankId}/adjustments", new
        {
            kind = "sweep",
            amount = 5m,
            date = Feb1,
            toBankAccountId = setup.DepositBankId,
            sourceRef = Key(),
        }, ct);
        await ShouldBeProblemAsync(intoTrust, HttpStatusCode.UnprocessableEntity, "sweep_bank_invalid",
            "A fee sweep moves management fees into the PM operating account, and the account it goes to is not one.", ct);

        var future = await client.PostAsJsonAsync(url, new
        {
            kind = "sweep",
            amount = 5m,
            date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3),
            toBankAccountId = setup.OperatingBankId,
            sourceRef = Key(),
        }, ct);
        future.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        using (var problem = System.Text.Json.JsonDocument.Parse(await future.Content.ReadAsStringAsync(ct)))
        {
            problem.RootElement.GetProperty("code").GetString().ShouldBe("posting_date_in_future");
        }

        await PostOkAsync<PostResult>(client, url, new
        {
            kind = "sweep",
            amount = 20m,
            date = Feb1,
            toBankAccountId = setup.OperatingBankId,
            sourceRef = Key(),
        }, ct);

        var trust = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        var operating = await GetAsync<RegisterResponse>(
            client, $"/api/accounting/banks/{setup.OperatingBankId}/register", ct);
        (trust.Totals.Book, operating.Totals.Book).ShouldBe((100m, 20m));
        var after = await GetAsync<HeldFeesView>(
            client, $"/api/accounting/banks/{setup.TrustBankId}/held-fees?from=2026-02-01", ct);
        (after.Held, after.Floor).ShouldBe((100m, 0m));
    }

    /// <summary>
    /// An owner contribution over HTTP (#744), through the real Directory reads behind both ports: it posts
    /// on the owner the property belonged to on its date, across an ownership transfer, and raises that
    /// owner's operating balance and the bank; a property the owner did not hold that day, a bank that is
    /// not operating trust, a future date and a missing property are each refused with their own answer.
    /// </summary>
    [Fact]
    public async Task An_owner_contribution_lands_on_the_owner_of_the_property_on_its_date()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);
        var url = $"/api/accounting/banks/{setup.TrustBankId}/adjustments";
        var feb5 = new DateOnly(2026, 2, 5);
        var feb12 = new DateOnly(2026, 2, 12);

        Guid seller = default, buyer = default, property = default;
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsSystemAsync(setup.OrgId, "test-harness", async () =>
            {
                seller = await sender.Send(new CreateOwner("Seller Holdings", null, null, null, null, 0m), ct);
                buyer = await sender.Send(new CreateOwner("Buyer Partners", null, null, null, null, 0m), ct);
                property = await sender.Send(new CreateProperty(seller, "1 Transfer Way", null, null, null, null), ct);
                (await sender.Send(new TransferPropertyOwnership(property, buyer, new DateOnly(2026, 2, 10), Key()), ct))
                    .ShouldNotBeNull();
            }, ct);
        }

        object Body(Guid owner, DateOnly date, decimal amount = 300m, Guid? propertyId = null) => new
        {
            kind = "owner-contribution",
            amount,
            date,
            ownerId = owner,
            propertyId = propertyId ?? property,
            sourceRef = Key(),
        };

        await PostOkAsync<PostResult>(client, url, Body(seller, feb5), ct);
        await PostOkAsync<PostResult>(client, url, Body(buyer, feb12, 200m), ct);

        foreach (var (owner, date) in new[] { (seller, feb12), (buyer, feb5) })
        {
            await ShouldBeProblemAsync(await client.PostAsJsonAsync(url, Body(owner, date), ct),
                HttpStatusCode.UnprocessableEntity, "property_not_owned",
                "This property does not belong to this owner on that date. Choose one of the owner's properties.", ct);
        }

        foreach (var bank in new[] { setup.DepositBankId, setup.OperatingBankId })
        {
            await ShouldBeProblemAsync(
                await client.PostAsJsonAsync($"/api/accounting/banks/{bank}/adjustments", Body(seller, feb5), ct),
                HttpStatusCode.UnprocessableEntity, "contribution_bank_not_operating_trust",
                "An owner contribution is paid into an operating trust account, and this account is not one.", ct);
        }

        var future = await client.PostAsJsonAsync(url, Body(buyer, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3)), ct);
        future.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        using (var problem = System.Text.Json.JsonDocument.Parse(await future.Content.ReadAsStringAsync(ct)))
        {
            problem.RootElement.GetProperty("code").GetString().ShouldBe("posting_date_in_future");
        }

        var noProperty = await client.PostAsJsonAsync(url,
            new { kind = "owner-contribution", amount = 5m, date = feb5, ownerId = seller, sourceRef = Key() }, ct);
        noProperty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var register = await GetAsync<RegisterResponse>(client, $"/api/accounting/banks/{setup.TrustBankId}/register", ct);
        register.Totals.Book.ShouldBe(500m);
        var sellerDetail = await GetAsync<OwnerDetail>(client, $"/api/directory/owners/{seller}", ct);
        var buyerDetail = await GetAsync<OwnerDetail>(client, $"/api/directory/owners/{buyer}", ct);
        (sellerDetail.Operating, buyerDetail.Operating).ShouldBe((300m, 200m));

        // Each lands in the Contributions section of its owner's statement and of the property's.
        foreach (var (owner, amount) in new[] { (seller, 300m), (buyer, 200m) })
        {
            foreach (var scope in new[] { "", $"&propertyId={property}" })
            {
                var body = await client.GetStringAsync($"/api/statements/{owner}?year=2026&month=2{scope}", ct);
                using var statement = System.Text.Json.JsonDocument.Parse(body);
                ContributionsSubtotal(statement.RootElement).ShouldBe(amount, body);
            }
        }
    }

    /// <summary>The subtotal of the statement's "Owner contributions" section, wherever the view nests it.</summary>
    private static decimal? ContributionsSubtotal(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (element.TryGetProperty("title", out var title) && title.ValueKind == System.Text.Json.JsonValueKind.String
                && title.GetString() == "Owner contributions" && element.TryGetProperty("subtotal", out var subtotal))
            {
                return subtotal.GetDecimal();
            }

            foreach (var property in element.EnumerateObject())
            {
                if (ContributionsSubtotal(property.Value) is { } found)
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContributionsSubtotal(item) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static async Task ShouldBeProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code, string detail, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(status, body);
        using var problem = System.Text.Json.JsonDocument.Parse(body);
        (problem.RootElement.GetProperty("code").GetString(), problem.RootElement.GetProperty("detail").GetString())
            .ShouldBe((code, detail));
    }

    private sealed record Setup(Guid OrgId, string Email, Guid TrustBankId, Guid OperatingBankId, Guid DepositBankId);

    private static string Key() => UuidV7.NewId().ToString();

    private async Task<Setup> SetupAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Bank HTTP Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"renee-{orgId:N}@example.com";
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

        Guid trustBankId = default;
        Guid operatingBankId = default;
        Guid depositBankId = default;
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsSystemAsync(orgId, "test-harness", async () =>
            {
                trustBankId = (await sender.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct)).Id;
                operatingBankId = (await sender.Send(
                    new CreateBankAccount("Management Operating", null, null, "operating"), ct)).Id;
                depositBankId = (await sender.Send(new CreateBankAccount("Deposit Trust", null, null, "deposit"), ct)).Id;
            }, ct);
        }

        return new Setup(orgId, email, trustBankId, operatingBankId, depositBankId);
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

    private static async Task<T> PostOkAsync<T>(HttpClient client, string url, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(url, body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url, CancellationToken ct)
    {
        var response = await client.GetAsync(url, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }
}
