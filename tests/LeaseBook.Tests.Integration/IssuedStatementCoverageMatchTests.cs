using LeaseBook.Modules.Accounting.Features.Statements;
using LeaseBook.Web.Reporting;
using Shouldly;
using static LeaseBook.Web.Reporting.IssuedStatementCoverage;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// Pure rule tests (no database) for #377's exact matching: an issued statement is reported only when
/// ADR-045's carry-forward will actually pick the posting up.
/// </summary>
public sealed class IssuedStatementCoverageMatchTests
{
    private static readonly Guid Owner = Guid.Parse("018f0000-0000-7000-8000-00000000a001");
    private static readonly Guid OtherOwner = Guid.Parse("018f0000-0000-7000-8000-00000000a002");
    private static readonly Guid PropertyA = Guid.Parse("018f0000-0000-7000-8000-00000000b001");
    private static readonly Guid PropertyB = Guid.Parse("018f0000-0000-7000-8000-00000000b002");

    private static readonly DateTime Posted = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime IssuedBefore = new(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);

    private static OwnerEquityLine Line(
        string basis, DateOnly date, Guid? property = null, decimal amount = 10.00m, Guid? entry = null) =>
        new(entry ?? Guid.NewGuid(), Owner, property ?? PropertyA, basis, date, Posted, amount);

    private static IssuedStatement Issued(
        int year, int month, string basis, Guid? property = null, DateTime? asOf = null, Guid? owner = null) =>
        new(owner ?? Owner, year, month, basis, property, asOf ?? IssuedBefore);

    [Fact]
    public void A_line_on_one_basis_never_matches_a_statement_on_the_other()
    {
        Match([Line("accrual", new(2026, 8, 20))], [Issued(2026, 8, "cash")]).ShouldBeEmpty();
        Match([Line("cash", new(2026, 8, 20))], [Issued(2026, 8, "accrual")]).ShouldBeEmpty();
    }

    [Fact]
    public void A_both_basis_line_matches_the_cash_and_the_accrual_statement_separately()
    {
        var matches = Match([Line("both", new(2026, 8, 20))], [Issued(2026, 8, "cash"), Issued(2026, 8, "accrual")]);

        matches.Select(m => m.Basis).ShouldBe(["accrual", "cash"]);
    }

    [Fact]
    public void Scope_is_whole_owner_or_the_lines_own_property()
    {
        var line = Line("accrual", new(2026, 8, 20), PropertyA);

        Match([line], [Issued(2026, 8, "accrual", property: PropertyB)])
            .ShouldBeEmpty("a statement scoped to another property never shows this line");
        Match([line], [Issued(2026, 8, "accrual", property: PropertyA)])
            .ShouldHaveSingleItem().PropertyId.ShouldBe(PropertyA);
        Match([line], [Issued(2026, 8, "accrual")])
            .ShouldHaveSingleItem().PropertyId.ShouldBeNull();
    }

    [Fact]
    public void Only_statements_for_the_entrys_month_or_later_count_and_the_latest_is_reported()
    {
        var line = Line("cash", new(2026, 8, 20));

        Match([line], [Issued(2026, 7, "cash")]).ShouldBeEmpty("July's statement never shows an August entry");

        var match = Match([line], [Issued(2026, 8, "cash"), Issued(2026, 9, "cash"), Issued(2026, 7, "cash")])
            .ShouldHaveSingleItem();
        (match.IssuedYear, match.IssuedMonth).ShouldBe((2026, 9));
    }

    [Fact]
    public void A_statement_whose_figures_were_read_after_the_posting_already_includes_it()
    {
        var line = Line("accrual", new(2026, 8, 20));

        Match([line], [Issued(2026, 8, "accrual", asOf: Posted.AddMinutes(5))]).ShouldBeEmpty();
        Match([line], [Issued(2026, 8, "accrual", asOf: Posted)])
            .ShouldBeEmpty("an equal instant is treated as included — the same rule the carry-forward itemizes by");
    }

    [Fact]
    public void Another_owners_statement_never_matches()
    {
        Match([Line("cash", new(2026, 8, 20))], [Issued(2026, 8, "cash", owner: OtherOwner)]).ShouldBeEmpty();
    }

    /// <summary>
    /// Netting follows the statement's reading, not the stored basis or property: lines that cancel within
    /// one entry under the statement's filter move nothing it shows — the carry-forward hides that zero
    /// section, so the notice must not claim otherwise.
    /// </summary>
    [Fact]
    public void Lines_that_cancel_under_the_statements_reading_do_not_match_it()
    {
        var entry = Guid.NewGuid();

        // +X on `both` and −X on `accrual`: an accrual statement reads both and nets to zero; a cash
        // statement reads only the `both` line, which does move it.
        var basisPair = new[]
        {
            Line("both", new(2026, 8, 20), amount: 25.00m, entry: entry),
            Line("accrual", new(2026, 8, 20), amount: -25.00m, entry: entry),
        };
        Match(basisPair, [Issued(2026, 8, "accrual")]).ShouldBeEmpty();
        Match(basisPair, [Issued(2026, 8, "cash")]).ShouldHaveSingleItem().Basis.ShouldBe("cash");

        // +X on property A and −X on property B: whole-owner nets to zero; each property statement moves.
        var scopePair = new[]
        {
            Line("cash", new(2026, 8, 20), PropertyA, 40.00m, entry),
            Line("cash", new(2026, 8, 20), PropertyB, -40.00m, entry),
        };
        Match(scopePair, [Issued(2026, 8, "cash")]).ShouldBeEmpty();
        Match(scopePair, [Issued(2026, 8, "cash", property: PropertyB)]).ShouldHaveSingleItem();
    }

    [Fact]
    public void Several_lines_for_one_owner_basis_and_scope_collapse_to_one_row()
    {
        var matches = Match(
            [Line("cash", new(2026, 8, 3)), Line("cash", new(2026, 9, 1))],
            [Issued(2026, 9, "cash")]);

        matches.ShouldHaveSingleItem();
    }
}
