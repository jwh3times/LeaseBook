using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting.Migration;

/// <summary>
/// WP-7 §3.1 (held-fees ADR-020 §5): a <c>pm_income</c> opening position is correct in exactly one
/// shape — a pm_income credit + <c>migration_clearing</c> contra, both basis <c>both</c>, dimensioned
/// to a trust bank, with no owner. Every other shape still posts and balances (I1-invisible) yet
/// corrupts the trust equation (I2) later, so <see cref="IBalanceForward.PostOpeningPositionAsync"/>
/// rejects the wrong shapes where they are created (S1) with an S2-clean
/// <see cref="InvalidOpeningPositionException"/> (message names no account code / id). The happy path
/// proves the authored shape posts and makes <c>held_pm_fees</c> non-vacuous in the equation.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class HeldFeesOpeningTests(PostgresFixture fixture)
{
    private static readonly DateOnly Cutover = new(2026, 6, 30);

    private static OpeningPositionRequest HeldFees(Guid bankId, decimal h,
        EntryBasis basis = EntryBasis.Both, Guid? ownerId = null, Guid? overrideBank = null) =>
        new(AccountCodes.PmIncome, Debit: null, Credit: new Money(h), basis,
            Cutover, $"opening:{Cutover:yyyy-MM-dd}:held-fees={overrideBank ?? bankId}",
            OwnerId: ownerId, BankAccountId: overrideBank ?? bankId);

    [Fact]
    public async Task Posts_the_authored_shape_and_makes_I2_non_vacuous()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var bf = (IBalanceForward)Events(scope);

        await scope.RunAsync(async () =>
        {
            // Bank book 750 = held fees 750 → trust equation holds with held_pm_fees == 750.
            await bf.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.TrustBank(scope.TrustBankId), Debit: new Money(750m), Credit: null,
                EntryBasis.Both, Cutover, $"opening:{Cutover:yyyy-MM-dd}:bank={scope.TrustBankId}",
                BankAccountId: scope.TrustBankId), ct);
            var heldId = await bf.PostOpeningPositionAsync(HeldFees(scope.TrustBankId, 750m), ct);

            // Shape: exactly pm_income CR + clearing DR, both Both, bank-dimensioned, owner null.
            var lines = await LinesOfAsync(scope.Db, heldId, ct);   // small local SQL helper: entry's lines + account code/class
            lines.Count.ShouldBe(2);
            var real = lines.Single(l => l.AccountClass == "pm_income");
            real.Credit.ShouldBe(750m); real.Debit.ShouldBeNull();
            real.Basis.ShouldBe("both");
            real.BankAccountId.ShouldBe(scope.TrustBankId);
            real.OwnerId.ShouldBeNull();
            var contra = lines.Single(l => l.AccountClass == "migration_clearing");
            contra.Debit.ShouldBe(750m); contra.Basis.ShouldBe("both");

            // I2 non-vacuous + clean; I5 clean; I3 sweep zero rows.
            var eq = await new GetTrustEquationHandler(scope.Db).Handle(new GetTrustEquation(), ct);
            var row = eq.Rows.Single(r => r.BankAccountId == scope.TrustBankId);
            row.HeldPmFees.ShouldBe(750m);
            row.Variance.ShouldBe(0m);
            (await new InvariantChecks(scope.Db).CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);
    }

    [Theory]
    [InlineData("accrual_basis")]
    [InlineData("null_bank")]
    [InlineData("operating_bank")]
    [InlineData("owner_dim")]
    public async Task Wrong_shapes_are_rejected_at_post_time(string variant)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var owner = UuidV7.NewId();
        await EnsureDirectoryAsync(fixture, scope, ct, owners: [owner]);
        var bf = (IBalanceForward)Events(scope);

        await scope.RunAsync(async () =>
        {
            var req = variant switch
            {
                "accrual_basis" => HeldFees(scope.TrustBankId, 100m, basis: EntryBasis.Accrual),
                "null_bank" => new OpeningPositionRequest(AccountCodes.PmIncome, null, new Money(100m),
                    EntryBasis.Both, Cutover, "opening:2026-06-30:held-fees=none"),
                "operating_bank" => HeldFees(scope.TrustBankId, 100m, overrideBank: scope.OperatingBankId),
                "owner_dim" => HeldFees(scope.TrustBankId, 100m, ownerId: owner),
                _ => throw new ArgumentOutOfRangeException(nameof(variant)),
            };
            var ex = await Should.ThrowAsync<InvalidOpeningPositionException>(
                () => bf.PostOpeningPositionAsync(req, ct));
            ex.Code.ShouldBe(variant switch
            {
                "accrual_basis" => "held_fees_basis_must_be_both",
                "null_bank" => "held_fees_bank_required",
                "operating_bank" => "held_fees_bank_not_trust",
                _ => "pm_income_owner_dimension",
            });
            // S2: the message names no account code, no GUID.
            ex.Message.ShouldNotContain("pm_income");
            ex.Message.ShouldNotContain(scope.TrustBankId.ToString());
        }, ct);
    }

    [Fact]
    public async Task Fee_report_excludes_openings_and_their_voids_symmetrically()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var bf = (IBalanceForward)Events(scope);

        await scope.RunAsync(async () =>
        {
            var heldId = await bf.PostOpeningPositionAsync(HeldFees(scope.TrustBankId, 300m), ct);

            // Cutover-month report: the opening is position, not flow (D10).
            var handler = new GetManagementFeeIncomeHandler(scope.Db);
            var june = await handler.Handle(new GetManagementFeeIncome(2026, 6), ct);
            june.Rows.ShouldBeEmpty("a held-fees opening must not read as June fee income");

            // Supersede symmetry (R13): void the opening — the month must not read −300 either.
            await Reversal(scope).ReverseAsync(heldId, "supersede test", Cutover, ct);
            var juneAfterVoid = await handler.Handle(new GetManagementFeeIncome(2026, 6), ct);
            juneAfterVoid.Rows.ShouldBeEmpty("a voided opening's mirror must be excluded symmetrically");
        }, ct);
    }

    /// <summary>
    /// One entry's lines projected to account class + amounts/basis/dims via raw SQL (snake_case
    /// aliases map to the record's PascalCase members under the context's snake-case convention). Class
    /// comes from the joined <c>accounts</c> row, amounts/basis/dims from the line.
    /// </summary>
    private static Task<List<HeldFeesLineView>> LinesOfAsync(DbContext db, Guid entryId, CancellationToken ct) =>
        db.Database.SqlQuery<HeldFeesLineView>(
            $"""
            SELECT a."class" AS account_class, jl.debit, jl.credit, jl.basis,
                   jl.bank_account_id, jl.owner_id
            FROM journal_lines jl
            JOIN accounts a ON a.id = jl.account_id
            WHERE jl.entry_id = {entryId}
            """).ToListAsync(ct);

    private sealed record HeldFeesLineView(
        string AccountClass, decimal? Debit, decimal? Credit, string Basis,
        Guid? BankAccountId, Guid? OwnerId);
}
