using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Reporting;
using LeaseBook.Web.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #377 over real seeded rows. The scenario org issues O-S1's May accrual statement, then posts a
/// May-dated recharge afterwards and voids it as of June 1 (ADR-045's demo). The recharge is exactly the
/// case the notice exists for; its void, every cash posting, and everything posted before a statement
/// was issued are exactly the cases it must stay quiet about.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class IssuedStatementCoverageEndpointTests(PostgresFixture fixture)
{
    private const string LateRechargeRef = "scenario:recharge:late:2026-05:t-s3";
    private const string LateRechargeVoidRef = "scenario:void:recharge:late:t-s3";

    [Fact]
    public async Task A_posting_after_the_May_statement_was_issued_reports_that_statement()
    {
        var ct = TestContext.Current.CancellationToken;
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);
        var recharge = await EntryIdAsync(LateRechargeRef, ct);

        var response = await (await AdminClientAsync(ct)).GetAsync(
            $"/api/statements/issued-coverage?entryIds={recharge}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        var body = await response.Content.ReadFromJsonAsync<IssuedStatementCoverageResponse>(ct);
        var row = body!.Rows.ShouldHaveSingleItem();
        row.OwnerName.ShouldBe("Harborview Holdings");
        row.Basis.ShouldBe("accrual");
        row.PropertyId.ShouldBeNull("May was issued whole-owner");
        row.PropertyAddress.ShouldBeNull();
        (row.IssuedYear, row.IssuedMonth).ShouldBe((2026, 5));
    }

    [Fact]
    public async Task A_June_dated_void_is_not_covered_by_the_May_statement()
    {
        var ct = TestContext.Current.CancellationToken;
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);
        var voidEntry = await EntryIdAsync(LateRechargeVoidRef, ct);

        var body = await (await AdminClientAsync(ct))
            .GetFromJsonAsync<IssuedStatementCoverageResponse>(
                $"/api/statements/issued-coverage?entryIds={voidEntry}", ct);

        body!.Rows.ShouldBeEmpty("no statement has been issued for June or later");
    }

    /// <summary>
    /// A run is looked up by its id: every posted item's entry plus each disbursement's separate fee
    /// entry. May's disbursement run posted before any May statement was issued, so it covers nothing —
    /// the as-of rule, on real rows.
    /// </summary>
    [Fact]
    public async Task A_run_resolves_its_entries_including_fee_entries_and_reports_nothing_issued_after()
    {
        var ct = TestContext.Current.CancellationToken;
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);

        var (runId, postedItems, entryIds) = await InOrgAsync(async db =>
        {
            var run = await db.Set<BulkRun>()
                .SingleAsync(r => r.RunType == RunType.Disbursement && r.PeriodYear == 2026 && r.PeriodMonth == 5, ct);
            var posted = await db.Set<BulkRunItem>().CountAsync(i => i.RunId == run.Id && i.Status == RunItemStatus.Posted, ct);
            return (run.Id, posted, await IssuedStatementCoverage.RunEntryIdsAsync(db, run.Id, ct));
        }, ct);

        postedItems.ShouldBeGreaterThan(0);
        entryIds.Count.ShouldBeGreaterThan(postedItems, "a fee-bearing disbursement contributes its fee entry too");
        entryIds.ShouldBeUnique();

        var body = await (await AdminClientAsync(ct))
            .GetFromJsonAsync<IssuedStatementCoverageResponse>($"/api/statements/issued-coverage?runId={runId}", ct);
        body!.Rows.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("?entryIds=018f0000-0000-7000-8000-000000000001&runId=018f0000-0000-7000-8000-000000000002")]
    public async Task Exactly_one_input_is_required(string query)
    {
        var ct = TestContext.Current.CancellationToken;
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);

        var response = await (await AdminClientAsync(ct)).GetAsync($"/api/statements/issued-coverage{query}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("title").GetString().ShouldBe("coverage_input_invalid");
    }

    [Fact]
    public async Task Requires_authentication()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await fixture.Api.CreateClient().GetAsync(
            "/api/statements/issued-coverage?runId=018f0000-0000-7000-8000-000000000002", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private Task<Guid> EntryIdAsync(string sourceRef, CancellationToken ct) =>
        InOrgAsync(db => db.Set<JournalEntry>().Where(e => e.SourceRef == sourceRef).Select(e => e.Id).SingleAsync(ct), ct);

    private async Task<T> InOrgAsync<T>(Func<AppDbContext, Task<T>> query, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        T result = default!;
        await executor.RunAsSystemAsync(ScenarioSeeder.ScenarioOrgId, "test-harness", async () => result = await query(db), ct);
        return result;
    }

    private async Task<HttpClient> AdminClientAsync(CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(ScenarioSeeder.AdminEmail, ScenarioSeeder.AdminPassword), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.PrimeCsrfAsync(ct);
        return client;
    }
}
