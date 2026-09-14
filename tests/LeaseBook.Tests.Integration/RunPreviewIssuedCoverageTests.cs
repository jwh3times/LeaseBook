using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Operations.Runs;
using LeaseBook.Modules.Reporting.Delivery;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Operations;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Reporting;
using LeaseBook.Web.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// Uses its own database because the equivalence test deliberately advances the immutable scenario
/// timeline through a July run. Sharing the ordinary integration fixture would contaminate the
/// golden scenario assertions that expect exactly its three seeded statement artifacts.
/// </summary>
[CollectionDefinition(nameof(RunPreviewIssuedCoverageCollection), DisableParallelization = true)]
public sealed class RunPreviewIssuedCoverageCollection : ICollectionFixture<PostgresFixture>
{
}

/// <summary>
/// #380 over the all-scenario fixture. The pre-confirm read is checked against #377's independently
/// persisted, post-confirm read so a drift between prospective and actual postings has nowhere to hide.
/// </summary>
[Collection(nameof(RunPreviewIssuedCoverageCollection))]
public sealed class RunPreviewIssuedCoverageTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Rent_preview_coverage_equals_the_confirmed_runs_issued_coverage()
    {
        var ct = TestContext.Current.CancellationToken;
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);

        // July has not been run by the scenario timeline. Issue a July snapshot first so the rent
        // postings below are exactly the late postings ADR-045 carries into the following statement.
        await InOrgAsync(async db =>
        {
            var may = await db.Set<StatementArtifact>()
                .SingleAsync(a => a.PeriodYear == 2026 && a.PeriodMonth == 5 && a.Basis == "accrual", ct);
            db.Set<StatementArtifact>().Add(new StatementArtifact
            {
                Id = UuidV7.NewId(),
                OwnerId = may.OwnerId,
                PeriodYear = 2026,
                PeriodMonth = 7,
                Basis = "accrual",
                PropertyId = null,
                EndingBalance = may.EndingBalance,
                AsOf = DateTime.UtcNow.AddMinutes(-1),
                ArtifactKey = "test-run-preview-issued-coverage.pdf",
            });
            await db.SaveChangesAsync(ct);
        }, ct);

        var client = await AdminClientAsync(fixture.Api, ct);
        var prospective = await client.GetFromJsonAsync<RunPreviewIssuedCoverageResponse>(
            "/api/operations/runs/rent/preview/issued-coverage?year=2026&month=7", ct);

        prospective.ShouldNotBeNull();
        prospective.Rows.ShouldNotBeEmpty();
        prospective.Rows.ShouldAllBe(row => row.OwnerName == "Harborview Holdings");

        var preview = await client.GetFromJsonAsync<RunPreviewSpaResponse>(
            "/api/operations/runs/rent/preview?year=2026&month=7", ct);
        preview.ShouldNotBeNull();

        var selectedTargetIds = prospective.Rows.Select(row => row.TargetId).Distinct().ToArray();
        var confirmedResponse = await client.PostAsJsonAsync(
            "/api/operations/runs/rent/confirm",
            new ConfirmRunRequest(2026, 7, selectedTargetIds, preview.CapabilitiesVersion), ct);
        confirmedResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await confirmedResponse.Content.ReadAsStringAsync(ct));
        var confirmed = await confirmedResponse.Content.ReadFromJsonAsync<RunResultSpaResponse>(ct);

        var actual = await client.GetFromJsonAsync<IssuedStatementCoverageResponse>(
            $"/api/statements/issued-coverage?runId={confirmed!.RunId}", ct);

        actual.ShouldNotBeNull();
        actual.Rows.ShouldBe(
            prospective.Rows
                .Select(row => new IssuedStatementCoverageRow(
                    row.OwnerId,
                    row.OwnerName,
                    row.Basis,
                    row.PropertyId,
                    row.PropertyAddress,
                    row.IssuedYear,
                    row.IssuedMonth))
                .Distinct()
                .OrderBy(row => row.OwnerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Basis, StringComparer.Ordinal)
                .ThenBy(row => row.PropertyAddress, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task No_candidate_issued_statement_returns_empty_without_asking_the_strategy_to_plan()
    {
        var ct = TestContext.Current.CancellationToken;
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);

        using var factory = fixture.Api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRunStrategy>();
            services.AddScoped<IRunStrategy, ThrowingRunStrategy>();
        }));
        var client = await AdminClientAsync(factory, ct);

        var response = await client.GetAsync(
            "/api/operations/runs/rent/preview/issued-coverage?year=2100&month=1", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        var body = await response.Content.ReadFromJsonAsync<RunPreviewIssuedCoverageResponse>(ct);
        body!.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Planning_failure_is_an_HTTP_failure_instead_of_false_empty_coverage()
    {
        var ct = TestContext.Current.CancellationToken;
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);
        var issuedOwnerId = await InOrgAsync(
            db => db.Set<StatementArtifact>()
                .Where(a => a.Basis != null && a.EndingBalance != null && a.AsOf != null)
                .Select(a => a.OwnerId)
                .FirstAsync(ct),
            ct);

        using var factory = fixture.Api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRunStrategy>();
            services.AddScoped<IRunStrategy>(_ => new ThrowingRunStrategy(issuedOwnerId));
        }));
        var client = await AdminClientAsync(factory, ct);

        var response = await client.GetAsync(
            "/api/operations/runs/rent/preview/issued-coverage?year=2026&month=5", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
        problem.GetProperty("code").GetString().ShouldBe("internal_error");
    }

    private async Task InOrgAsync(Func<AppDbContext, Task> action, CancellationToken ct) =>
        await InOrgAsync(async db =>
        {
            await action(db);
            return true;
        }, ct);

    private async Task<T> InOrgAsync<T>(Func<AppDbContext, Task<T>> action, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        T result = default!;
        await executor.RunAsSystemAsync(
            ScenarioSeeder.ScenarioOrgId, "test-harness", async () => result = await action(db), ct);
        return result;
    }

    private static async Task<HttpClient> AdminClientAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        CancellationToken ct)
    {
        var client = factory.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(ScenarioSeeder.AdminEmail, ScenarioSeeder.AdminPassword), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.PrimeCsrfAsync(ct);
        return client;
    }

    private sealed class ThrowingRunStrategy(
        Guid? ownerId = null) : IRunStrategy
    {
        public RunType RunType => RunType.Rent;

        public Task<StrategyPreview> PreviewAsync(RunPeriod period, CancellationToken ct) =>
            Task.FromResult(new StrategyPreview(
            [
                new PreviewRow(
                    RunTargetKind.Lease,
                    Guid.Parse("01993000-0000-7000-8000-000000000001"),
                    "Eligible target for an owner with no issued statement",
                    100m,
                    AlreadyDone: false,
                    ExcludedReason: null,
                    Detail: new Dictionary<string, string>(),
                    OwnerId: ownerId ?? Guid.Parse("01993000-0000-7000-8000-000000000002")),
            ],
            []));

        public Task<IReadOnlyList<RunPlanItem>> PlanAsync(
            RunPeriod period, IReadOnlyList<Guid> selectedTargetIds, CancellationToken ct) =>
            throw new InvalidOperationException("The issued-statement precheck should skip run planning.");
    }
}
