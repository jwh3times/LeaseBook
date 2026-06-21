using LeaseBook.Modules.Banking.Import;
using Shouldly;

namespace LeaseBook.Tests.Banking;

/// <summary>
/// The CSV statement parser (P66 / ADR-015): maps an arbitrary bank CSV to normalized statement rows via a
/// column map. A single signed <c>Amount</c> column or a <c>Debit</c>/<c>Credit</c> pair; a customer-account
/// sign convention (credit = money in = +, debit = money out = −); row-level error tolerance (a bad row is
/// reported, never fatal). OFX/QFX is a later parser behind the same <see cref="IStatementParser"/> seam.
/// </summary>
public sealed class CsvStatementParserTests
{
    private readonly IStatementParser _parser = new CsvStatementParser();

    [Fact]
    public void Maps_a_signed_amount_column()
    {
        const string csv =
            "Date,Description,Amount\n" +
            "2026-02-01,ACH DEPOSIT RENT,1500.00\n" +
            "2026-02-03,BANK SERVICE FEE,-25.00\n";

        var result = _parser.Parse(csv, new ColumnMap(Date: "Date", Description: "Description", Amount: "Amount"));

        result.Errors.ShouldBeEmpty();
        result.Rows.Count.ShouldBe(2);
        result.Rows[0].ShouldBe(new ParsedRow(1, new DateOnly(2026, 2, 1), "ACH DEPOSIT RENT", 1500.00m));
        result.Rows[1].ShouldBe(new ParsedRow(2, new DateOnly(2026, 2, 3), "BANK SERVICE FEE", -25.00m));
    }

    [Fact]
    public void Maps_separate_debit_and_credit_columns_to_a_signed_amount()
    {
        // Customer-account convention: a credit increases the balance (deposit, +); a debit decreases it (−).
        const string csv =
            "Posted,Memo,Debit,Credit\n" +
            "02/01/2026,Owner draw,200.00,\n" +
            "02/05/2026,Interest,,1.25\n";

        var result = _parser.Parse(csv, new ColumnMap(
            Date: "Posted", Description: "Memo", Debit: "Debit", Credit: "Credit", DateFormat: "MM/dd/yyyy"));

        result.Errors.ShouldBeEmpty();
        result.Rows[0].Amount.ShouldBe(-200.00m);
        result.Rows[1].Amount.ShouldBe(1.25m);
    }

    [Fact]
    public void Reports_a_row_whose_amount_exceeds_two_decimal_places()
    {
        // Money is NUMERIC(14,2); a third significant decimal would round silently downstream (P28),
        // so the parser rejects it at the boundary rather than letting it reach the journal.
        const string csv =
            "Date,Description,Amount\n" +
            "2026-02-01,Fine,100.00\n" +
            "2026-02-02,Too precise,12.345\n";

        var result = _parser.Parse(csv, new ColumnMap(Date: "Date", Description: "Description", Amount: "Amount"));

        result.Rows.Count.ShouldBe(1);
        result.Errors.ShouldHaveSingleItem().RowNumber.ShouldBe(2);
    }

    [Fact]
    public void Reports_a_bad_row_without_dropping_the_good_ones()
    {
        const string csv =
            "Date,Description,Amount\n" +
            "2026-02-01,Good row,100.00\n" +
            "not-a-date,Bad date,50.00\n" +
            "2026-02-02,Another good,75.00\n";

        var result = _parser.Parse(csv, new ColumnMap(Date: "Date", Description: "Description", Amount: "Amount"));

        result.Rows.Count.ShouldBe(2);
        result.Rows.Select(r => r.Description).ShouldBe(["Good row", "Another good"]);
        result.Errors.Count.ShouldBe(1);
        result.Errors[0].RowNumber.ShouldBe(2);
    }
}
