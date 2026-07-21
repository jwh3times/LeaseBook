using System.Net;
using System.Net.Http.Json;
using System.Text;
using LeaseBook.Migrator.Model;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Directory.Domain;
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
/// WP-7 Task 5: the pre-sign-off corrected re-import engine (<see cref="BalanceImportService.SupersedeAsync"/>).
///
/// The engine diffs a corrected CSV against the live opening positions (per family, keyed by base
/// source_ref) and, per changed family, posts a linked reversal dated at the cutover then a corrected
/// revision at the next <c>#r{N}</c>; identical figures are left untouched (S3 idempotency); a
/// correction to $0.00 reverses without re-posting. The three §2 guards
/// (already_signed_off / nothing_to_supersede / cutover_date_mismatch) throw a typed
/// <see cref="SupersedeConflictException"/> before any write.
///
/// Arrange drives the real HTTP import path; the engine itself is invoked through a scoped provider
/// inside <see cref="OrgScopedExecutor"/> (the ambient RLS transaction), mirroring a request unit-of-work.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class BalanceSupersedeTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";
    private static readonly DateOnly Cutover = new(2026, 6, 30);
    private const string CutoverStr = "2026-06-30";

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // 1. Changed figure → reversal (void mirror dated at cutover) + #r2 revision; clearing re-zeroes.
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Changed_figure_posts_reversal_and_revision_and_clearing_rezeros()
    {
        var ct = TestContext.Current.CancellationToken;
        var (setup, ownerId) = await ArrangeTiedSetAsync("Changed", ct);

        // Supersede owner O-1: cash 500 → 450, accrual 500 → 450 (accrual delta stays 0).
        var result = await SupersedeInScopeAsync(setup.OrgId, EntityKind.OwnerBalances,
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,450.00,450.00\n", Cutover, ct);

        result.Counts.Superseded.ShouldBe(1);
        result.Counts.Unchanged.ShouldBe(0);

        var baseRef = $"opening:{CutoverStr}:owner-equity={ownerId}";
        await ReadAsync(setup.OrgId, async db =>
        {
            var family = await FamilyEntriesAsync(db, baseRef, ct);

            // Original opening entry — now reversed, still credit 500.
            var original = family.Single(r => r.SourceRef == baseRef);
            original.EventType.ShouldBe("OpeningBalance");
            original.IsReversed.ShouldBeTrue("the superseded original must carry a linked reversal");
            original.Credit.ShouldBe(500.00m);

            // EntryVoided mirror — dated at the cutover (2026-06-30), credit/debit swapped.
            var mirror = family.Single(r => r.SourceRef == baseRef + ":void");
            mirror.EventType.ShouldBe("EntryVoided");
            mirror.EntryDate.ShouldBe(Cutover, "the reversal is dated at the cutover, never today");
            mirror.Debit.ShouldBe(500.00m);

            // #r2 revision — the corrected credit 450.
            var r2 = family.Single(r => r.SourceRef == baseRef + "#r2");
            r2.EventType.ShouldBe("OpeningBalance");
            r2.Credit.ShouldBe(450.00m);
            r2.IsReversed.ShouldBeFalse();

            // Net owner-equity cash position folds to the corrected 450 (500 − 500 + 450).
            var cashEquity = await OwnerEquityNetAsync(db, ownerId, "cash", "both", ct);
            cashEquity.ShouldBe(450.00m);

            // The deliberate remaining gap: equity dropped 50 but the bank still reads 1000 → residual 50.
            var cashNet = await OnboardingTestHelpers.ClearingNetAsync(db, "cash", ct);
            Math.Abs(cashNet).ShouldBe(50.00m, "the 50 equity correction leaves a 50 clearing residual until the bank is fixed");
        }, ct);

        // Correct the bank side too: trust bank 500 → 450 (total bank 450 + 500 deposit = 950 = equity
        // 450 + deposit-liability 500) → clearing re-zeroes in BOTH bases.
        await SupersedeInScopeAsync(setup.OrgId, EntityKind.BankBalances,
            $"Account ID,Account Name,Book Balance\nB-TRUST,{setup.TrustBankName},450.00\nB-DEP,{setup.DepositBankName},500.00\n",
            Cutover, ct);

        await ReadAsync(setup.OrgId, async db =>
        {
            (await OnboardingTestHelpers.ClearingNetAsync(db, "cash", ct)).ShouldBe(0m,
                "cash clearing re-zeroes once the bank is corrected to match equity");
            (await OnboardingTestHelpers.ClearingNetAsync(db, "accrual", ct)).ShouldBe(0m,
                "accrual clearing re-zeroes once the bank is corrected to match equity");
        }, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // 2. Identical corrected file → every row unchanged, nothing re-posted (S3).
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unchanged_rows_are_not_reposted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (setup, _) = await ArrangeTiedSetAsync("Unchanged", ct);

        var (journalBefore, _) = await CountsAsync(setup.OrgId, ct);

        // Supersede with the identical original figures.
        var result = await SupersedeInScopeAsync(setup.OrgId, EntityKind.OwnerBalances,
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,500.00,500.00\n", Cutover, ct);

        result.Counts.Unchanged.ShouldBe(result.RowCount, "an identical file leaves every row unchanged");
        result.Counts.Superseded.ShouldBe(0);

        var (journalAfter, _) = await CountsAsync(setup.OrgId, ct);
        journalAfter.ShouldBe(journalBefore, "an identical corrected file must re-post no journal entries");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // 3. Cash-only change moves the accrual-delta family too (F2).
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cash_only_change_moves_the_accrual_delta_family()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("AccrualDelta", ct);
        await OnboardingTestHelpers.ImportOwnerTenantChainAsync(setup.Client, ct);

        // Original: cash 500 / accrual 700 → cash CR 500 (Both) + accrual-delta CR 200 (Accrual).
        await OnboardingTestHelpers.PostBalanceImportAsync<ImportBatchResult>(setup.Client, "owner_balances",
            new { csvContent = "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,500.00,700.00\n", cutoverDate = CutoverStr, filename = "owners.csv" }, ct);
        var ownerId = await ResolveOwnerIdAsync(setup.OrgId, "Chain Owner LLC", ct);

        // Supersede: cash 500 → 600, accrual held at 700 → new delta 100 (700 − 600).
        var result = await SupersedeInScopeAsync(setup.OrgId, EntityKind.OwnerBalances,
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,600.00,700.00\n", Cutover, ct);
        result.Counts.Superseded.ShouldBe(1);

        var cashBase = $"opening:{CutoverStr}:owner-equity={ownerId}";
        var accrualBase = $"opening:{CutoverStr}:owner-equity-accrual={ownerId}";
        await ReadAsync(setup.OrgId, async db =>
        {
            // Cash family folds to the corrected 600.
            (await OwnerEquityNetAsync(db, ownerId, "cash", "both", ct)).ShouldBe(600.00m);

            // Accrual-delta family folds to 100 (200 − 200 + 100).
            (await OwnerEquityNetAsync(db, ownerId, "accrual", "accrual", ct)).ShouldBe(100.00m);

            // A #r2 accrual revision exists carrying the corrected 100 delta.
            var accrualFamily = await FamilyEntriesAsync(db, accrualBase, ct);
            var accrualR2 = accrualFamily.Single(r => r.SourceRef == accrualBase + "#r2");
            accrualR2.EventType.ShouldBe("OpeningBalance");
            accrualR2.Basis.ShouldBe("accrual");
            accrualR2.Credit.ShouldBe(100.00m);

            // Total accrual-basis owner equity ties to 700.00 exactly (cash 600 both + 100 accrual).
            (await OwnerEquityNetAsync(db, ownerId, "accrual", "both", ct)).ShouldBe(700.00m);

            // Sanity: the cash family's #r2 exists too (proves both families moved, not just accrual).
            var cashFamily = await FamilyEntriesAsync(db, cashBase, ct);
            cashFamily.ShouldContain(r => r.SourceRef == cashBase + "#r2" && r.Credit == 600.00m);
        }, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // 4. Correction to $0.00 reverses without re-posting (D8 removal).
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Corrected_to_zero_reverses_without_reposting()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("ToZero", ct);
        await OnboardingTestHelpers.ImportOwnerTenantChainAsync(setup.Client, ct);

        // Original deposit liability: T-1 held 500.
        await OnboardingTestHelpers.PostBalanceImportAsync<ImportBatchResult>(setup.Client, "deposit_liabilities",
            new { csvContent = "Tenant ID,Owner ID,Deposit Held\nT-1,O-1,500.00\n", cutoverDate = CutoverStr, filename = "deposits.csv" }, ct);

        // Supersede: held 500 → 0.00.
        var result = await SupersedeInScopeAsync(setup.OrgId, EntityKind.DepositLiabilities,
            "Tenant ID,Owner ID,Deposit Held\nT-1,O-1,0.00\n", Cutover, ct);
        result.Counts.Superseded.ShouldBe(1);

        await ReadAsync(setup.OrgId, async db =>
        {
            // All deposit-family entries for the org (there is exactly one tenant).
            var family = await FamilyEntriesAsync(db, $"opening:{CutoverStr}:deposit=", ct);

            // The original is reversed; a void mirror exists; NO #r2 was posted.
            family.ShouldContain(r => !r.SourceRef.Contains("#r") && !r.SourceRef.EndsWith(":void")
                                      && r.EventType == "OpeningBalance" && r.IsReversed);
            family.ShouldContain(r => r.SourceRef.EndsWith(":void") && r.EventType == "EntryVoided");
            family.ShouldNotContain(r => r.SourceRef.Contains("#r"), "a $0.00 correction must not post a revision");

            // The deposit-liability family nets to $0.00 (credit 500 − debit 500), in both bases.
            var net = family.Sum(r => (r.Credit ?? 0m) - (r.Debit ?? 0m));
            net.ShouldBe(0m, "reversing the only deposit entry leaves the family flat");
            family.ShouldAllBe(r => r.Basis == "both");
        }, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // 5. Signed-off org → typed conflict, no side effect.
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Signed_off_org_returns_conflict_with_no_side_effect()
    {
        var ct = TestContext.Current.CancellationToken;
        var (setup, _) = await ArrangeTiedSetAsync("SignedOff", ct);

        // Verify + sign off through the real endpoints (as the m7 verification tests do).
        var (trustBankId, depositBankId) = await ResolveBankIdsAsync(setup.OrgId, ct);
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
        report.IsTied.ShouldBeTrue();

        var signoffResponse = await setup.Client.PostAsJsonAsync(
            $"/api/onboarding/verification/{report.VerificationId}/signoff", new { }, ct);
        signoffResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await signoffResponse.Content.ReadAsStringAsync(ct));

        var (journalBefore, batchesBefore) = await CountsAsync(setup.OrgId, ct);

        var ex = await Should.ThrowAsync<SupersedeConflictException>(async () =>
            await SupersedeInScopeAsync(setup.OrgId, EntityKind.OwnerBalances,
                "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,450.00,450.00\n", Cutover, ct));
        ex.Code.ShouldBe("already_signed_off");

        var (journalAfter, batchesAfter) = await CountsAsync(setup.OrgId, ct);
        journalAfter.ShouldBe(journalBefore, "the signed-off guard must leave the journal untouched");
        batchesAfter.ShouldBe(batchesBefore, "the signed-off guard must not add an import batch");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // 6. Cutover-date mismatch → typed conflict, no side effect.
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cutover_date_mismatch_is_a_typed_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var (setup, _) = await ArrangeTiedSetAsync("DateMismatch", ct);

        var (journalBefore, batchesBefore) = await CountsAsync(setup.OrgId, ct);

        var ex = await Should.ThrowAsync<SupersedeConflictException>(async () =>
            await SupersedeInScopeAsync(setup.OrgId, EntityKind.OwnerBalances,
                "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,450.00,450.00\n",
                new DateOnly(2026, 7, 31), ct));
        ex.Code.ShouldBe("cutover_date_mismatch");

        var (journalAfter, batchesAfter) = await CountsAsync(setup.OrgId, ct);
        journalAfter.ShouldBe(journalBefore, "the cutover-mismatch guard must leave the journal untouched");
        batchesAfter.ShouldBe(batchesBefore, "the cutover-mismatch guard must not add an import batch");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // 7. Fresh org with no prior balance batch → typed conflict.
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nothing_to_supersede_is_a_typed_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("Nothing", ct);

        var ex = await Should.ThrowAsync<SupersedeConflictException>(async () =>
            await SupersedeInScopeAsync(setup.OrgId, EntityKind.OwnerBalances,
                "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Ghost Owner,450.00,450.00\n", Cutover, ct));
        ex.Code.ShouldBe("nothing_to_supersede");

        var (_, batches) = await CountsAsync(setup.OrgId, ct);
        batches.ShouldBe(0, "the nothing-to-supersede guard must not add an import batch");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // 8. Two successive supersedes → #r2 then #r3, N derived from the journal.
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_successive_supersedes_yield_r2_then_r3()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("Successive", ct);
        await OnboardingTestHelpers.ImportOwnerTenantChainAsync(setup.Client, ct);

        // Original owner cash/accrual 500 (delta 0, so only the cash family moves).
        await OnboardingTestHelpers.PostBalanceImportAsync<ImportBatchResult>(setup.Client, "owner_balances",
            new { csvContent = "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,500.00,500.00\n", cutoverDate = CutoverStr, filename = "owners.csv" }, ct);
        var ownerId = await ResolveOwnerIdAsync(setup.OrgId, "Chain Owner LLC", ct);

        // 500 → 450 → 480.
        await SupersedeInScopeAsync(setup.OrgId, EntityKind.OwnerBalances,
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,450.00,450.00\n", Cutover, ct);
        await SupersedeInScopeAsync(setup.OrgId, EntityKind.OwnerBalances,
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,480.00,480.00\n", Cutover, ct);

        var baseRef = $"opening:{CutoverStr}:owner-equity={ownerId}";
        await ReadAsync(setup.OrgId, async db =>
        {
            var family = await FamilyEntriesAsync(db, baseRef, ct);

            // #r2 exists and is now reversed (superseded by #r3).
            var r2 = family.Single(r => r.SourceRef == baseRef + "#r2");
            r2.EventType.ShouldBe("OpeningBalance");
            r2.IsReversed.ShouldBeTrue("#r2 was superseded by the second correction");

            // #r3 is the live revision carrying 480.00.
            var r3 = family.Single(r => r.SourceRef == baseRef + "#r3");
            r3.EventType.ShouldBe("OpeningBalance");
            r3.IsReversed.ShouldBeFalse();
            r3.Credit.ShouldBe(480.00m);

            // The revision count N is journal-derived: exactly one live cash position remains, at 480.
            (await OwnerEquityNetAsync(db, ownerId, "cash", "both", ct)).ShouldBe(480.00m);
        }, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // 9. Successor batch records lineage + an audit event.
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Successor_batch_records_lineage_and_audit_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var (setup, _) = await ArrangeTiedSetAsync("Lineage", ct);

        Guid originalBatchId = default;
        await ReadAsync(setup.OrgId, async db =>
        {
            originalBatchId = await db.Set<ImportBatch>()
                .Where(b => b.EntityKind == "OwnerBalances")
                .OrderBy(b => b.CreatedAt)
                .Select(b => b.Id)
                .FirstAsync(ct);
        }, ct);

        var result = await SupersedeInScopeAsync(setup.OrgId, EntityKind.OwnerBalances,
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,450.00,450.00\n", Cutover, ct);
        result.Counts.Superseded.ShouldBe(1);

        await ReadAsync(setup.OrgId, async db =>
        {
            var successor = await db.Set<ImportBatch>()
                .Where(b => b.EntityKind == "OwnerBalances")
                .OrderByDescending(b => b.CreatedAt)
                .FirstAsync(ct);
            successor.Id.ShouldBe(result.BatchId);
            successor.SupersedesBatchId.ShouldBe(originalBatchId, "the successor records the batch it corrects");
            successor.Status.ShouldBe("posted");

            var audit = await db.Set<AuditEvent>()
                .Where(a => a.EntityType == "import-superseded" && a.EntityId == successor.Id)
                .FirstOrDefaultAsync(ct);
            audit.ShouldNotBeNull("a supersede must write an 'import-superseded' audit event");
            audit!.Action.ShouldBe("insert");
        }, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // 10. HTTP: the real supersede route returns 200 with the superseded count (WP-7 Task 6).
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Http_supersede_route_returns_200_and_superseded_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var (setup, _) = await ArrangeTiedSetAsync("HttpHappy", ct);

        var response = await setup.Client.PostAsJsonAsync(
            "/api/onboarding/import-balances/owner_balances/supersede",
            new
            {
                csvContent = "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,450.00,450.00\n",
                cutoverDate = CutoverStr,
                filename = "owners.csv",
            }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        var result = (await response.Content.ReadFromJsonAsync<ImportBatchResult>(ct))!;
        result.Counts.Superseded.ShouldBe(1);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // 11. HTTP: a signed-off org gets a typed 409 problem with a code + correlationId (WP-7 Task 6).
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Http_supersede_route_returns_409_for_signed_off_org()
    {
        var ct = TestContext.Current.CancellationToken;
        var (setup, _) = await ArrangeTiedSetAsync("HttpConflict", ct);

        // Verify + sign off through the real endpoints (mirrors Signed_off_org_returns_conflict_with_no_side_effect).
        var (trustBankId, depositBankId) = await ResolveBankIdsAsync(setup.OrgId, ct);
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
        report.IsTied.ShouldBeTrue();

        var signoffResponse = await setup.Client.PostAsJsonAsync(
            $"/api/onboarding/verification/{report.VerificationId}/signoff", new { }, ct);
        signoffResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await signoffResponse.Content.ReadAsStringAsync(ct));

        var response = await setup.Client.PostAsJsonAsync(
            "/api/onboarding/import-balances/owner_balances/supersede",
            new
            {
                csvContent = "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,450.00,450.00\n",
                cutoverDate = CutoverStr,
                filename = "owners.csv",
            }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemWithCode>(ct))!;
        problem.Code.ShouldBe("already_signed_off");
        problem.CorrelationId.ShouldNotBeNullOrWhiteSpace(
            "every error response must carry a correlationId the operator can quote (ADR-025)");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // 12. WP-7 Task 10: held_pm_fees participates in the same family/supersede machinery as every
    //     other balance kind (ADR-020 §5) — the planner arm alone made this work, no supersede-code
    //     change was needed.
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Held_fees_supersede_participates_in_the_family_machinery()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("HeldFeesSupersede", ct);
        await OnboardingTestHelpers.ImportOwnerTenantChainAsync(setup.Client, ct);

        // The §5 acceptance shape: trust bank book 600.00 = owner equity 500.00 + held fees 100.00.
        await OnboardingTestHelpers.PostBalanceImportAsync<ImportBatchResult>(setup.Client, "bank_balances",
            new
            {
                csvContent = $"Account ID,Account Name,Book Balance\nB-TRUST,{setup.TrustBankName},600.00\n",
                cutoverDate = CutoverStr,
                filename = "banks.csv",
            }, ct);
        await OnboardingTestHelpers.PostBalanceImportAsync<ImportBatchResult>(setup.Client, "owner_balances",
            new
            {
                csvContent = "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,500.00,500.00\n",
                cutoverDate = CutoverStr,
                filename = "owners.csv",
            }, ct);

        var (trustBankId, _) = await ResolveBankIdsAsync(setup.OrgId, ct);

        var heldResult = await OnboardingTestHelpers.PostBalanceImportAsync<ImportBatchResult>(setup.Client, "held_pm_fees",
            new
            {
                csvContent = $"Account ID,Account Name,Held Fees\nB-TRUST,{setup.TrustBankName},100.00\n",
                cutoverDate = CutoverStr,
                filename = "held_fees.csv",
            }, ct);
        heldResult.ErrorCount.ShouldBe(0);

        await ReadAsync(setup.OrgId, async db =>
        {
            (await OnboardingTestHelpers.ClearingNetAsync(db, "cash", ct)).ShouldBe(0m,
                "the tied set (bank 600 = equity 500 + held fees 100) zeroes the residual before the supersede");
            (await OnboardingTestHelpers.ClearingNetAsync(db, "accrual", ct)).ShouldBe(0m);
        }, ct);

        // Supersede: held fees 100 → 80, and the bank book down to 580 to keep the set tied (the
        // tied variant — clearing re-zeroes rather than showing the 20.00 shift).
        var result = await SupersedeInScopeAsync(setup.OrgId, EntityKind.HeldPmFees,
            $"Account ID,Account Name,Held Fees\nB-TRUST,{setup.TrustBankName},80.00\n", Cutover, ct);
        result.Counts.Superseded.ShouldBe(1);

        await SupersedeInScopeAsync(setup.OrgId, EntityKind.BankBalances,
            $"Account ID,Account Name,Book Balance\nB-TRUST,{setup.TrustBankName},580.00\n", Cutover, ct);

        var baseRef = $"opening:{CutoverStr}:held-fees={trustBankId}";
        await ReadAsync(setup.OrgId, async db =>
        {
            var family = await FamilyEntriesAsync(db, baseRef, ct);

            // Original opening entry — now reversed, still credit 100 (the original recorded value).
            var original = family.Single(r => r.SourceRef == baseRef);
            original.EventType.ShouldBe("OpeningBalance");
            original.IsReversed.ShouldBeTrue("the superseded original must carry a linked reversal");
            original.Credit.ShouldBe(100.00m);

            // #r2 revision — the corrected credit 80.
            var r2 = family.Single(r => r.SourceRef == baseRef + "#r2");
            r2.EventType.ShouldBe("OpeningBalance");
            r2.Credit.ShouldBe(80.00m);
            r2.IsReversed.ShouldBeFalse();

            // The family (original + void mirror + #r2) nets to the corrected 80.
            var net = family.Sum(r => (r.Credit ?? 0m) - (r.Debit ?? 0m));
            net.ShouldBe(80.00m, "the held-fees family nets to the corrected 80.00");

            // Both corrections landed (bank 580 = equity 500 + held 80) → clearing re-zeroes.
            (await OnboardingTestHelpers.ClearingNetAsync(db, "cash", ct)).ShouldBe(0m,
                "held fees down 20 + bank book down 20 keeps the set tied");
            (await OnboardingTestHelpers.ClearingNetAsync(db, "accrual", ct)).ShouldBe(0m);
        }, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // 13. D8: a same-kind family left OUT of the corrected file is untouched (omission ≠ removal).
    //     The headline semantic of the file-scoped engine — structurally guaranteed today (nothing
    //     iterates absent positions, and there is no removal path), but nothing pinned it.
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Omitted_same_kind_family_is_left_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("Omission", ct);

        // Two owners of the SAME kind, so the omitted one is a peer of the corrected one — not merely
        // a different balance kind the file never addressed.
        await OnboardingTestHelpers.PostImportAsync<ImportBatchResult>(setup.Client, "owners",
            new
            {
                csvContent = "Owner ID,Owner Name,Reserve\nO-1,Chain Owner LLC,0\nO-2,Omitted Owner LLC,0\n",
                filename = "owners.csv",
            }, ct);

        await OnboardingTestHelpers.PostBalanceImportAsync<ImportBatchResult>(setup.Client, "owner_balances",
            new
            {
                csvContent = "Owner ID,Owner Name,Cash Balance,Accrual Balance\n"
                             + "O-1,Chain Owner LLC,500.00,500.00\nO-2,Omitted Owner LLC,700.00,700.00\n",
                cutoverDate = CutoverStr,
                filename = "owners.csv",
            }, ct);

        var correctedId = await ResolveOwnerIdAsync(setup.OrgId, "Chain Owner LLC", ct);
        var omittedId = await ResolveOwnerIdAsync(setup.OrgId, "Omitted Owner LLC", ct);

        // The corrected file carries O-1 only. O-2 is absent — which must mean "leave it alone",
        // never "remove it" (the $0.00 resubmission in test 4 is the removal gesture).
        var result = await SupersedeInScopeAsync(setup.OrgId, EntityKind.OwnerBalances,
            "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,450.00,450.00\n", Cutover, ct);

        result.RowCount.ShouldBe(1, "the batch spans the corrected file's rows only, not the live position set");
        result.Counts.Superseded.ShouldBe(1);
        result.Counts.Unchanged.ShouldBe(0, "the omitted family is absent from the batch, not counted as unchanged");
        result.Counts.Posted.ShouldBe(0);
        result.Counts.Skipped.ShouldBe(0);

        var omittedBase = $"opening:{CutoverStr}:owner-equity={omittedId}";
        var correctedBase = $"opening:{CutoverStr}:owner-equity={correctedId}";
        await ReadAsync(setup.OrgId, async db =>
        {
            // The omitted family is exactly as imported: one live entry, no void mirror, no revision.
            var omitted = await FamilyEntriesAsync(db, omittedBase, ct);
            var live = omitted.ShouldHaveSingleItem();
            live.SourceRef.ShouldBe(omittedBase);
            live.EventType.ShouldBe("OpeningBalance");
            live.IsReversed.ShouldBeFalse("an omitted family must never be reversed");
            live.Credit.ShouldBe(700.00m);
            (await OwnerEquityNetAsync(db, omittedId, "cash", "both", ct)).ShouldBe(700.00m,
                "the omitted owner's live position is unmoved");

            // …while the family that WAS in the file moved, proving the run did real work.
            var corrected = await FamilyEntriesAsync(db, correctedBase, ct);
            corrected.ShouldContain(r => r.SourceRef == correctedBase + ":void");
            corrected.ShouldContain(r => r.SourceRef == correctedBase + "#r2" && r.Credit == 450.00m);
            (await OwnerEquityNetAsync(db, correctedId, "cash", "both", ct)).ShouldBe(450.00m);
        }, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // 14. A held-fees shape violation mid-supersede aborts the WHOLE batch — it must never commit a
    //     reversal without its revision. The reversal is flushed before the repost runs
    //     (SaveChanges-per-post), so converting the throw to a row error would silently remove the
    //     live position while reporting the row as failed. Only reachable through reference-data
    //     divergence, which is what this test arranges deliberately.
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Held_fees_shape_violation_mid_supersede_rolls_the_whole_batch_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync("ShapeRollback", ct);
        await OnboardingTestHelpers.ImportOwnerTenantChainAsync(setup.Client, ct);

        // A clean held-fees position against the properly-provisioned trust bank.
        await OnboardingTestHelpers.PostBalanceImportAsync<ImportBatchResult>(setup.Client, "held_pm_fees",
            new
            {
                csvContent = $"Account ID,Account Name,Held Fees\nB-TRUST,{setup.TrustBankName},100.00\n",
                cutoverDate = CutoverStr,
                filename = "held_fees.csv",
            }, ct);

        var (trustBankId, _) = await ResolveBankIdsAsync(setup.OrgId, ct);
        var baseRef = $"opening:{CutoverStr}:held-fees={trustBankId}";

        // Diverge the reference data: the bank keeps its Trust purpose (so the planner still accepts
        // the row) but its chart entry is no longer trust_bank-class — exactly the corruption the
        // post-time shape guard exists to catch. The CHECK constraint on accounts allows this swap
        // because pm_operating_bank also carries a bank_account_id.
        await ReadAsync(setup.OrgId, async db =>
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE accounts SET class = 'pm_operating_bank' WHERE bank_account_id = {trustBankId} AND class = 'trust_bank'",
                ct);
        }, ct);

        var (journalBefore, batchesBefore) = await CountsAsync(setup.OrgId, ct);

        // The corrected figure differs, so the engine reverses the live entry and THEN reposts —
        // the repost trips the guard. The exception must escape rather than becoming a row error.
        // Fully qualified: an unaliased `using` for Accounting.Contracts would make BankPurpose
        // ambiguous against Directory.Domain's, which ResolveBankIdsAsync below depends on.
        var ex = await Should.ThrowAsync<LeaseBook.Modules.Accounting.Contracts.InvalidOpeningPositionException>(async () =>
            await SupersedeInScopeAsync(setup.OrgId, EntityKind.HeldPmFees,
                $"Account ID,Account Name,Held Fees\nB-TRUST,{setup.TrustBankName},80.00\n", Cutover, ct));
        ex.Code.ShouldBe("held_fees_bank_not_trust");

        var (journalAfter, batchesAfter) = await CountsAsync(setup.OrgId, ct);
        journalAfter.ShouldBe(journalBefore, "the aborted supersede must leave the journal untouched");
        batchesAfter.ShouldBe(batchesBefore, "the aborted supersede must not add an import batch");

        await ReadAsync(setup.OrgId, async db =>
        {
            var family = await FamilyEntriesAsync(db, baseRef, ct);

            // The decisive assertion: no orphan reversal survived. The original is still live at 100.
            family.ShouldNotContain(r => r.SourceRef.EndsWith(":void", StringComparison.Ordinal),
                "a reversal must never commit without its corrected revision");
            var original = family.Single(r => r.SourceRef == baseRef);
            original.IsReversed.ShouldBeFalse("the rolled-back reversal must not survive the abort");
            original.Credit.ShouldBe(100.00m, "the live held-fees position is exactly as imported");
        }, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Arrange + invoke helpers
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record TestSetup(Guid OrgId, HttpClient Client, string TrustBankName, string DepositBankName);

    /// <summary>Imports the Task-3 tied set: owner O-1 cash/accrual 500, deposit 500, trust + deposit banks 500 each.</summary>
    private async Task<(TestSetup Setup, Guid OwnerId)> ArrangeTiedSetAsync(string tag, CancellationToken ct)
    {
        var setup = await SetupAsync(tag, ct);
        await OnboardingTestHelpers.ImportOwnerTenantChainAsync(setup.Client, ct);

        await OnboardingTestHelpers.PostBalanceImportAsync<ImportBatchResult>(setup.Client, "bank_balances",
            new
            {
                csvContent = $"Account ID,Account Name,Book Balance\nB-TRUST,{setup.TrustBankName},500.00\nB-DEP,{setup.DepositBankName},500.00\n",
                cutoverDate = CutoverStr,
                filename = "banks.csv",
            }, ct);
        await OnboardingTestHelpers.PostBalanceImportAsync<ImportBatchResult>(setup.Client, "owner_balances",
            new { csvContent = "Owner ID,Owner Name,Cash Balance,Accrual Balance\nO-1,Chain Owner LLC,500.00,500.00\n", cutoverDate = CutoverStr, filename = "owners.csv" }, ct);
        await OnboardingTestHelpers.PostBalanceImportAsync<ImportBatchResult>(setup.Client, "deposit_liabilities",
            new { csvContent = "Tenant ID,Owner ID,Deposit Held\nT-1,O-1,500.00\n", cutoverDate = CutoverStr, filename = "deposits.csv" }, ct);

        var ownerId = await ResolveOwnerIdAsync(setup.OrgId, "Chain Owner LLC", ct);
        return (setup, ownerId);
    }

    /// <summary>
    /// Invokes <see cref="BalanceImportService.SupersedeAsync"/> through a scoped provider inside the
    /// ambient RLS transaction (commit on success, rollback on throw) — the non-HTTP equivalent of a
    /// request unit-of-work, so guard throws leave zero side effect.
    /// </summary>
    private async Task<ImportBatchResult> SupersedeInScopeAsync(
        Guid orgId, EntityKind kind, string csv, DateOnly cutover, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var service = scope.ServiceProvider.GetRequiredService<BalanceImportService>();

        ImportBatchResult result = null!;
        await executor.RunAsync(orgId, async () =>
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
            result = await service.SupersedeAsync(kind, "appfolio-default", $"{kind}.csv", cutover, stream, ct);
        }, ct);
        return result;
    }

    private async Task<TestSetup> SetupAsync(string tag, CancellationToken ct)
    {
        var orgId = UuidV7.NewId();

        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Balance Supersede Test {tag} {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"bal-supersede-{orgId:N}@example.com";
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
                DisplayName = "Balance Supersede Test User",
            };
            (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
        }

        var trustBankName = $"Operating Trust {orgId:N}";
        var depositBankName = $"Security Deposit Trust {orgId:N}";

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
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync(ct));
        await client.PrimeCsrfAsync(ct); // XSRF rotates on sign-in

        return new TestSetup(orgId, client, trustBankName, depositBankName);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // Read helpers (fresh org-scoped context each call — all reads are raw SQL, so always DB-fresh)
    // ──────────────────────────────────────────────────────────────────────────────────────────────

    private async Task ReadAsync(Guid orgId, Func<DbContext, Task> read, CancellationToken ct)
    {
        var tenant = new TenantContext { OrgId = orgId };
        await using var db = fixture.CreateContext(fixture.AppConnectionString, tenant);
        var executor = new OrgScopedExecutor(db, tenant);
        await executor.RunAsync(orgId, () => read(db), ct);
    }

    private async Task<(int Journal, int Batches)> CountsAsync(Guid orgId, CancellationToken ct)
    {
        var journal = 0;
        var batches = 0;
        await ReadAsync(orgId, async db =>
        {
            journal = await db.Set<JournalEntry>().CountAsync(ct);
            batches = await db.Set<ImportBatch>().CountAsync(ct);
        }, ct);
        return (journal, batches);
    }

    private async Task<Guid> ResolveOwnerIdAsync(Guid orgId, string name, CancellationToken ct)
    {
        var id = Guid.Empty;
        await ReadAsync(orgId, async db =>
        {
            id = await db.Set<Owner>().AsNoTracking()
                .Where(o => o.Name == name)
                .Select(o => o.Id)
                .SingleAsync(ct);
        }, ct);
        return id;
    }

    private async Task<(Guid TrustBankId, Guid DepositBankId)> ResolveBankIdsAsync(Guid orgId, CancellationToken ct)
    {
        var trustId = Guid.Empty;
        var depositId = Guid.Empty;
        await ReadAsync(orgId, async db =>
        {
            var banks = await db.Set<BankAccount>().AsNoTracking().Where(b => b.IsActive).ToListAsync(ct);
            trustId = banks.First(b => b.Purpose == BankPurpose.Trust).Id;
            depositId = banks.First(b => b.Purpose == BankPurpose.Deposit).Id;
        }, ct);
        return (trustId, depositId);
    }

    /// <summary>
    /// Sums owner_equity (credit − debit) for one owner over the two given bases (pass the same basis
    /// twice for an exact-basis sum). Reversal mirrors preserve owner_id, so they net the reversed leg out.
    /// </summary>
    private static async Task<decimal> OwnerEquityNetAsync(
        DbContext db, Guid ownerId, string basisA, string basisB, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<NetRow>(
            $"""
            SELECT COALESCE(SUM(COALESCE(jl.credit, 0) - COALESCE(jl.debit, 0)), 0) AS net
            FROM journal_lines jl
            JOIN accounts a ON a.id = jl.account_id
            WHERE a.code = 'owner_equity'
              AND jl.owner_id = {ownerId}
              AND jl.basis IN ({basisA}, {basisB})
            """).ToListAsync(ct);
        return rows.Count == 0 ? 0m : rows[0].Net;
    }

    /// <summary>
    /// Every real (non-clearing) leg whose entry source_ref starts with <paramref name="sourceRefPrefix"/>
    /// — one row per (entry, real leg). <c>IsReversed</c> = a linked reversal references the entry.
    /// </summary>
    private static async Task<List<FamilyRow>> FamilyEntriesAsync(
        DbContext db, string sourceRefPrefix, CancellationToken ct)
    {
        var like = sourceRefPrefix + "%";
        return await db.Database.SqlQuery<FamilyRow>(
            $"""
            SELECT e.source_ref AS source_ref,
                   e.event_type AS event_type,
                   e.entry_date AS entry_date,
                   EXISTS (SELECT 1 FROM journal_entries r WHERE r.reverses_entry_id = e.id) AS is_reversed,
                   jl.debit  AS debit,
                   jl.credit AS credit,
                   jl.basis  AS basis
            FROM journal_entries e
            JOIN journal_lines jl ON jl.entry_id = e.id
            WHERE e.source_ref LIKE {like}
              AND jl.account_class <> 'migration_clearing'
            ORDER BY e.source_ref
            """).ToListAsync(ct);
    }

    private sealed record NetRow(decimal Net);

    private sealed record FamilyRow(
        string SourceRef, string EventType, DateOnly EntryDate, bool IsReversed,
        decimal? Debit, decimal? Credit, string Basis);

    private sealed record ProblemWithCode(string Code, string? CorrelationId);
}
