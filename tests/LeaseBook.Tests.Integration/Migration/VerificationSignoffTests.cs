using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Onboarding;
using LeaseBook.Web.Onboarding.Persistence;
using LeaseBook.Web.Onboarding.Verification;
using LeaseBook.Web.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration.Migration;

/// <summary>
/// WP-4 Task 4.2: verification report + hard sign-off gate (M7).
///
/// Three scenarios:
/// 1. Tied import + matching operator figures → POST /verification returns IsTied=true, VarianceTotal=0;
///    POST /signoff → 200, signed row exists, audit_events row with entity_type='migration-signed-off'.
/// 2. Non-tying import → POST /verification returns IsTied=false; POST /signoff → 409 not_tied;
///    NO audit_events row written, SignedOffAt is still null on the original row.
/// 3. Clearing zero but external mismatch → IsTied=false (external match required, not just clearing).
///
/// Gate-before-side-effect: test 2 asserts that after 409, no audit row exists and no signed row was
/// inserted — mirroring the StatementNotBalancedException → 409 no-write guarantee (M5 WP-04).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class VerificationSignoffTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";
    private static readonly DateOnly Cutover = new(2026, 6, 30);
    private const string CutoverStr = "2026-06-30";

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Test 1: tied import + matching operator figures → IsTied=true, signoff succeeds + audit row
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tied_import_and_matching_operator_figures_verify_and_signoff_succeed()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("Tied", ct);

        // --- Entity imports (pre-requisites) ---
        await ImportOwnerTenantChainAsync(setup.Client, ct);

        // --- Tied balance imports: bank(trust=500, dep=500) == owner_equity(500) + deposit(500) ---
        await PostBalanceAsync(setup.Client, "bank_balances",
            $"Account ID,Account Name,Book Balance\nB-T,{setup.TrustBankName},500.00\nB-D,{setup.DepositBankName},500.00\n", ct);
        await PostBalanceAsync(setup.Client, "owner_balances",
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Tied Owner LLC,500.00,500.00\n", ct);
        await PostBalanceAsync(setup.Client, "deposit_liabilities",
            "Tenant ID,Owner ID,Deposit Held\nT-1,O-1,500.00\n", ct);

        // Resolve the trust + deposit bank account ids so we can supply them as operator figures.
        var (trustBankId, depositBankId) = await ResolveBankIdsAsync(setup.OrgId, ct);

        // --- POST /api/onboarding/verification with correct operator figures ---
        var verBody = new
        {
            cutoverDate = CutoverStr,
            ownerEquityTotal = 500.00m,
            depositLiabilityTotal = 500.00m,
            bankBookBalances = new[]
            {
                new { bankAccountId = trustBankId, expectedBook = 500.00m, accountCode = (string?)null },
                new { bankAccountId = depositBankId, expectedBook = 500.00m, accountCode = (string?)null },
            },
        };

        var verResponse = await setup.Client.PostAsJsonAsync("/api/onboarding/verification", verBody, ct);
        verResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await verResponse.Content.ReadAsStringAsync(ct));
        var report = (await verResponse.Content.ReadFromJsonAsync<VerificationReport>(ct))!;

        report.IsTied.ShouldBeTrue("tied import + matching operator figures must produce IsTied=true");
        report.VarianceTotal.ShouldBe(0m, "no variance expected");
        report.ClearingCash.ShouldBe(0m, "cash clearing must net to $0");
        report.ClearingAccrual.ShouldBe(0m, "accrual clearing must net to $0");

        // --- POST /api/onboarding/verification/{id}/signoff → 200 ---
        var signoffResponse = await setup.Client.PostAsJsonAsync(
            $"/api/onboarding/verification/{report.VerificationId}/signoff", new { }, ct);
        signoffResponse.StatusCode.ShouldBe(HttpStatusCode.OK,
            await signoffResponse.Content.ReadAsStringAsync(ct));
        var signoffResult = (await signoffResponse.Content.ReadFromJsonAsync<SignoffResult>(ct))!;

        signoffResult.SignedOffAt.ShouldNotBe(default);

        // --- Assert: signed row exists AND audit_events row with entity_type = 'migration-signed-off' ---
        var tenant = new TenantContext { OrgId = setup.OrgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        await executor.RunAsync(setup.OrgId, async () =>
        {
            // The signed row should be findable by the returned id.
            var signedRow = await db.Set<MigrationVerification>()
                .FirstOrDefaultAsync(v => v.Id == signoffResult.SignedVerificationId, ct);
            signedRow.ShouldNotBeNull("signed verification row must exist after sign-off");
            signedRow!.SignedOffBy.ShouldNotBeNullOrEmpty();
            signedRow.SignedOffAt.ShouldNotBeNull();
            signedRow.IsTied.ShouldBeTrue();

            // An explicit audit event with entity_type = 'migration-signed-off' must exist.
            var auditRow = await db.Set<AuditEvent>()
                .Where(a => a.EntityType == "migration-signed-off"
                            && a.EntityId == signoffResult.SignedVerificationId)
                .FirstOrDefaultAsync(ct);
            auditRow.ShouldNotBeNull(
                "an audit_events row with entity_type='migration-signed-off' must be written on successful sign-off");
            auditRow!.Action.ShouldBe("insert");
        }, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Test 2: non-tying import → 409 not_tied, gate fires BEFORE any side effect
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Non_tying_import_signoff_returns_409_and_writes_no_audit_row_or_signed_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("NonTying", ct);

        // Import only the bank balance — clearing will be non-zero (no owner_equity to offset).
        await PostBalanceAsync(setup.Client, "bank_balances",
            $"Account ID,Account Name,Book Balance\nB-T,{setup.TrustBankName},1000.00\n", ct);

        var (trustBankId, _) = await ResolveBankIdsAsync(setup.OrgId, ct);

        // POST /verification with operator figures that claim $0 equity — clearing non-zero, so IsTied=false.
        var verBody = new
        {
            cutoverDate = CutoverStr,
            ownerEquityTotal = 0m,
            depositLiabilityTotal = 0m,
            bankBookBalances = new[]
            {
                new { bankAccountId = trustBankId, expectedBook = 1000.00m, accountCode = (string?)null },
            },
        };

        var verResponse = await setup.Client.PostAsJsonAsync("/api/onboarding/verification", verBody, ct);
        verResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await verResponse.Content.ReadAsStringAsync(ct));
        var report = (await verResponse.Content.ReadFromJsonAsync<VerificationReport>(ct))!;

        report.IsTied.ShouldBeFalse("non-tying import must produce IsTied=false");

        // --- POST /signoff → must return 409 not_tied ---
        var signoffResponse = await setup.Client.PostAsJsonAsync(
            $"/api/onboarding/verification/{report.VerificationId}/signoff", new { }, ct);
        signoffResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict,
            "signoff must return 409 when IsTied=false");

        var problemBody = await signoffResponse.Content.ReadAsStringAsync(ct);
        problemBody.ShouldContain("not_tied");

        // --- Gate-before-side-effect: assert NO audit row and NO signed row were written ---
        var tenant = new TenantContext { OrgId = setup.OrgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        await executor.RunAsync(setup.OrgId, async () =>
        {
            // No 'migration-signed-off' audit event should exist.
            var auditCount = await db.Set<AuditEvent>()
                .CountAsync(a => a.EntityType == "migration-signed-off", ct);
            auditCount.ShouldBe(0,
                "NO audit_events row should be written when signoff is blocked by the not_tied gate");

            // The original verification row must still have SignedOffAt == null.
            var originalRow = await db.Set<MigrationVerification>()
                .FirstOrDefaultAsync(v => v.Id == report.VerificationId, ct);
            originalRow.ShouldNotBeNull();
            originalRow!.SignedOffAt.ShouldBeNull(
                "SignedOffAt must remain null — the gate fired before any write");
            originalRow.SignedOffBy.ShouldBeNull("SignedOffBy must remain null");

            // No signed row (IsTied=true + SignedOffAt set) should exist at all for this org.
            var signedRowCount = await db.Set<MigrationVerification>()
                .CountAsync(v => v.SignedOffAt != null, ct);
            signedRowCount.ShouldBe(0,
                "no signed verification row should exist when sign-off was blocked");
        }, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Test 3: clearing nets to $0 but operator figure for owner equity is deliberately off
    //         → IsTied=false (external mismatch, even though clearing is balanced)
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clearing_zero_but_operator_equity_mismatch_is_not_tied()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("ExternalMismatch", ct);

        // Import a tied set (clearing will be $0).
        await ImportOwnerTenantChainAsync(setup.Client, ct);
        await PostBalanceAsync(setup.Client, "bank_balances",
            $"Account ID,Account Name,Book Balance\nB-T,{setup.TrustBankName},500.00\nB-D,{setup.DepositBankName},500.00\n", ct);
        await PostBalanceAsync(setup.Client, "owner_balances",
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,500.00,500.00\n", ct);
        await PostBalanceAsync(setup.Client, "deposit_liabilities",
            "Tenant ID,Owner ID,Deposit Held\nT-1,O-1,500.00\n", ct);

        var (trustBankId, depositBankId) = await ResolveBankIdsAsync(setup.OrgId, ct);

        // Operator figure for owner_equity is deliberately wrong (600 instead of 500).
        var verBody = new
        {
            cutoverDate = CutoverStr,
            ownerEquityTotal = 600.00m,  // ← deliberately off by $100
            depositLiabilityTotal = 500.00m,
            bankBookBalances = new[]
            {
                new { bankAccountId = trustBankId, expectedBook = 500.00m, accountCode = (string?)null },
                new { bankAccountId = depositBankId, expectedBook = 500.00m, accountCode = (string?)null },
            },
        };

        var verResponse = await setup.Client.PostAsJsonAsync("/api/onboarding/verification", verBody, ct);
        verResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await verResponse.Content.ReadAsStringAsync(ct));
        var report = (await verResponse.Content.ReadFromJsonAsync<VerificationReport>(ct))!;

        // Clearing is $0 (internal balance is good), but external mismatch → IsTied must be false.
        report.ClearingCash.ShouldBe(0m, "clearing cash must be $0 after tied import");
        report.ClearingAccrual.ShouldBe(0m, "clearing accrual must be $0 after tied import");
        report.IsTied.ShouldBeFalse(
            "external equity mismatch ($100 variance) must make IsTied=false even though clearing is $0");
        report.VarianceTotal.ShouldBe(100m, "variance total should equal the $100 equity mismatch");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Test 4: stale verification — journal drifts AFTER a tied verify, sign-off re-derives and 409s
    //         (the verification IsTied flag is frozen true, but sign-off must NOT trust it)
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stale_verification_after_journal_drift_signoff_re_derives_and_returns_409()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("StaleDrift", ct);

        // --- Import a fully tied set ---
        await ImportOwnerTenantChainAsync(setup.Client, ct);
        await PostBalanceAsync(setup.Client, "bank_balances",
            $"Account ID,Account Name,Book Balance\nB-T,{setup.TrustBankName},500.00\nB-D,{setup.DepositBankName},500.00\n", ct);
        await PostBalanceAsync(setup.Client, "owner_balances",
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Tied Owner LLC,500.00,500.00\n", ct);
        await PostBalanceAsync(setup.Client, "deposit_liabilities",
            "Tenant ID,Owner ID,Deposit Held\nT-1,O-1,500.00\n", ct);

        var (trustBankId, depositBankId) = await ResolveBankIdsAsync(setup.OrgId, ct);

        // --- Verify: ties at this moment (IsTied=true, persisted) ---
        var verBody = new
        {
            cutoverDate = CutoverStr,
            ownerEquityTotal = 500.00m,
            depositLiabilityTotal = 500.00m,
            bankBookBalances = new[]
            {
                new { bankAccountId = trustBankId, expectedBook = 500.00m, accountCode = (string?)null },
                new { bankAccountId = depositBankId, expectedBook = 500.00m, accountCode = (string?)null },
            },
        };

        var verResponse = await setup.Client.PostAsJsonAsync("/api/onboarding/verification", verBody, ct);
        verResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await verResponse.Content.ReadAsStringAsync(ct));
        var report = (await verResponse.Content.ReadFromJsonAsync<VerificationReport>(ct))!;
        report.IsTied.ShouldBeTrue("the set ties at verification time");

        // --- DRIFT: import an ADDITIONAL receivable so the accrual clearing now nets non-zero ---
        // A receivable with no offsetting accrual delta breaks the accrual clearing tie-out.
        await PostBalanceAsync(setup.Client, "tenant_receivables",
            "Tenant ID,Owner ID,Balance Due\nT-1,O-1,250.00\n", ct);

        // --- Sign-off the STALE verification id: must re-derive against the now-drifted journal → 409 ---
        var signoffResponse = await setup.Client.PostAsJsonAsync(
            $"/api/onboarding/verification/{report.VerificationId}/signoff", new { }, ct);
        signoffResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict,
            "sign-off must re-derive tie-out against the current journal and 409 when it no longer ties");

        var problemBody = await signoffResponse.Content.ReadAsStringAsync(ct);
        problemBody.ShouldContain("not_tied");

        // --- No signed row, no audit row: the gate fired before any side effect ---
        var tenant = new TenantContext { OrgId = setup.OrgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        await executor.RunAsync(setup.OrgId, async () =>
        {
            var auditCount = await db.Set<AuditEvent>()
                .CountAsync(a => a.EntityType == "migration-signed-off", ct);
            auditCount.ShouldBe(0,
                "no migration-signed-off audit row when the re-derived tie-out blocks sign-off");

            var signedRowCount = await db.Set<MigrationVerification>()
                .CountAsync(v => v.SignedOffAt != null, ct);
            signedRowCount.ShouldBe(0, "no signed verification row when sign-off was blocked");

            // The original verification row still carries its frozen IsTied=true flag, untouched —
            // proving the gate did NOT trust the stale flag (it re-derived instead).
            var originalRow = await db.Set<MigrationVerification>()
                .FirstOrDefaultAsync(v => v.Id == report.VerificationId, ct);
            originalRow.ShouldNotBeNull();
            originalRow!.IsTied.ShouldBeTrue("the frozen flag stays true; the gate re-derives, not trusts it");
            originalRow.SignedOffAt.ShouldBeNull();
        }, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record TestSetup(
        Guid OrgId,
        HttpClient Client,
        string TrustBankName,
        string DepositBankName);

    private async Task<TestSetup> SetupAsync(string tag, CancellationToken ct)
    {
        var orgId = UuidV7.NewId();

        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Verification Test {tag} {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"ver-test-{orgId:N}@example.com";
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
                DisplayName = "Verification Test User",
            };
            (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
        }

        var trustBankName = $"Trust {orgId:N}";
        var depositBankName = $"Deposit {orgId:N}";

        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsync(orgId, async () =>
            {
                await sender.Send(new CreateBankAccount(trustBankName, null, null, "trust"), ct);
                await sender.Send(new CreateBankAccount(depositBankName, null, null, "deposit"), ct);
            }, ct);
        }

        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync(ct));
        await client.PrimeCsrfAsync(ct);

        return new TestSetup(orgId, client, trustBankName, depositBankName);
    }

    /// <summary>Resolves (trustBankId, depositBankId) for the given org via a direct DB read.</summary>
    private async Task<(Guid TrustBankId, Guid DepositBankId)> ResolveBankIdsAsync(
        Guid orgId, CancellationToken ct)
    {
        var tenant = new TenantContext { OrgId = orgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);

        Guid trustId = default;
        Guid depositId = default;

        await executor.RunAsync(orgId, async () =>
        {
            var banks = await db.Set<LeaseBook.Modules.Directory.Domain.BankAccount>()
                .AsNoTracking()
                .Where(b => b.IsActive)
                .ToListAsync(ct);

            trustId = banks.First(b => b.Purpose == LeaseBook.Modules.Directory.Domain.BankPurpose.Trust).Id;
            depositId = banks.First(b => b.Purpose == LeaseBook.Modules.Directory.Domain.BankPurpose.Deposit).Id;
        }, ct);

        return (trustId, depositId);
    }

    /// <summary>Imports one owner → property → unit → tenant/lease chain (same as BalanceImportTests).</summary>
    private static async Task ImportOwnerTenantChainAsync(HttpClient client, CancellationToken ct)
    {
        await PostImportAsync<ImportBatchResult>(client, "owners",
            new { csvContent = "Owner ID,Owner Name,Reserve\nO-1,Tied Owner LLC,0\n", filename = "owners.csv" }, ct);
        await PostImportAsync<ImportBatchResult>(client, "properties",
            new { csvContent = "Property ID,Owner ID,Address\nP-1,O-1,1 Tied St\n", filename = "properties.csv" }, ct);
        await PostImportAsync<ImportBatchResult>(client, "units",
            new { csvContent = "Unit ID,Property ID,Unit,Rent,Status\nUNIT-1,P-1,Unit A,1000.00,occupied\n", filename = "units.csv" }, ct);
        await PostImportAsync<ImportBatchResult>(client, "tenants_leases",
            new
            {
                csvContent = "Tenant ID,Unit ID,Tenant Name,Lease Start,Lease End,Rent,Deposit,Status\n" +
                             "T-1,UNIT-1,Tied Tenant,2025-01-01,,1000.00,500.00,active\n",
                filename = "tenants.csv",
            }, ct);
    }

    private static async Task PostBalanceAsync(HttpClient client, string kind, string csv, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/onboarding/import-balances/{kind}",
            new { csvContent = csv, cutoverDate = CutoverStr, filename = $"{kind}.csv" }, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task<T> PostImportAsync<T>(
        HttpClient client, string kind, object body, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync($"/api/onboarding/import/{kind}", body, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }
}
