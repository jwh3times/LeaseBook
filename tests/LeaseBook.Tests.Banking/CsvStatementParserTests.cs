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

    [Fact]
    public void A_stray_quote_in_a_description_does_not_cost_the_row()
    {
        const string csv =
            "Date,Description,Amount\n" +
            "2026-02-01,ACME 12\" PIPE CO,40.00\n";

        var result = _parser.Parse(csv, new ColumnMap(Date: "Date", Description: "Description", Amount: "Amount"));

        result.Errors.ShouldBeEmpty();
        result.Rows.ShouldHaveSingleItem().Amount.ShouldBe(40.00m);
    }

    public static TheoryData<ColumnMap, string, string> MapsNamingAnAbsentColumn => new()
    {
        { new ColumnMap(Date: "Posted", Description: "Description", Amount: "Amount"), "Date", "Posted" },
        { new ColumnMap(Date: "Date", Description: "Memo", Amount: "Amount"), "Description", "Memo" },
        { new ColumnMap(Date: "Date", Description: "Description", Amount: "Value"), "Amount", "Value" },
        { new ColumnMap(Date: "Date", Description: "Description", Debit: "Withdrawal", Credit: "Amount"), "Debit", "Withdrawal" },
        { new ColumnMap(Date: "Date", Description: "Description", Debit: "Amount", Credit: "Deposit"), "Credit", "Deposit" },
    };

    [Theory]
    [MemberData(nameof(MapsNamingAnAbsentColumn))]
    public void Refuses_a_map_naming_a_column_the_file_does_not_have(ColumnMap map, string field, string absent)
    {
        // #645: an absent column used to read as an empty field, so a missing amount column imported every
        // row at 0.00 with no error. The whole file is refused before any row is read.
        const string csv =
            "Date,Description,Amount\n" +
            "2026-02-01,ACH DEPOSIT RENT,1500.00\n";

        var result = _parser.Parse(csv, map);

        result.MissingColumns.ShouldBe([new MissingColumn(field, absent)]);
        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Names_every_absent_column()
    {
        const string csv = "Date,Description,Amount\n2026-02-01,Rent,1500.00\n";

        var result = _parser.Parse(csv, new ColumnMap(Date: "Posted", Description: "Memo", Debit: "Out", Credit: "In"));

        result.MissingColumns.ShouldBe(
        [
            new MissingColumn("Date", "Posted"),
            new MissingColumn("Description", "Memo"),
            new MissingColumn("Debit", "Out"),
            new MissingColumn("Credit", "In"),
        ]);
    }

    [Fact]
    public void A_blank_amount_beside_a_debit_and_credit_pair_maps_nothing()
    {
        const string csv = "Date,Description,Debit,Credit\n2026-02-01,Rent,,1500.00\n";

        var result = _parser.Parse(csv, new ColumnMap(
            Date: "Date", Description: "Description", Amount: "", Debit: "Debit", Credit: "Credit"));

        result.MissingColumns.ShouldBeEmpty();
        result.Rows.ShouldHaveSingleItem().Amount.ShouldBe(1500.00m);
    }

    [Fact]
    public void A_header_that_differs_only_in_case_is_absent()
    {
        const string csv = "Date,Description,amount\n2026-02-01,Rent,1500.00\n";

        var result = _parser.Parse(csv, new ColumnMap(Date: "Date", Description: "Description", Amount: "Amount"));

        result.MissingColumns.ShouldBe([new MissingColumn("Amount", "Amount")]);
        result.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void Matches_a_header_padded_with_spaces()
    {
        // The import wizard offers header names trimmed, so "Date, Description, Amount" maps as "Amount".
        const string csv = "Date, Description, Amount\n2026-02-01,Rent,1500.00\n";

        var result = _parser.Parse(csv, new ColumnMap(Date: "Date", Description: "Description", Amount: "Amount"));

        result.MissingColumns.ShouldBeEmpty();
        result.Rows.ShouldHaveSingleItem().Amount.ShouldBe(1500.00m);
    }

    [Fact]
    public void Matches_a_mapped_name_padded_with_spaces()
    {
        const string csv = "Date,Description,Amount\n2026-02-01,Rent,1500.00\n";

        var result = _parser.Parse(csv, new ColumnMap(Date: "Date", Description: "Description", Amount: " Amount "));

        result.MissingColumns.ShouldBeEmpty();
        result.Rows.ShouldHaveSingleItem().Amount.ShouldBe(1500.00m);
    }

    [Fact]
    public void Reads_the_first_column_of_a_file_that_starts_with_a_byte_order_mark()
    {
        const string csv =
            "\uFEFFDate,Description,Amount\n" +
            "2026-02-01,ACH DEPOSIT RENT,1500.00\n";

        var result = _parser.Parse(csv, new ColumnMap(Date: "Date", Description: "Description", Amount: "Amount"));

        result.Errors.ShouldBeEmpty();
        result.Rows.ShouldHaveSingleItem().Date.ShouldBe(new DateOnly(2026, 2, 1));
    }

    [Fact]
    public void Reports_a_row_too_short_to_reach_its_amount()
    {
        // #650: a field past the end of a short row read as empty, so the row was stored at 0.00 with no error.
        const string csv =
            "Date,Description,Amount\n" +
            "2026-02-01,Good row,100.00\n" +
            "2026-02-02,Rent\n" +
            "2026-02-03,Another good,75.00\n";

        var result = _parser.Parse(csv, new ColumnMap(Date: "Date", Description: "Description", Amount: "Amount"));

        result.Rows.Select(r => r.Description).ShouldBe(["Good row", "Another good"]);
        result.Errors.ShouldBe([new RowError(2, "amount is empty.")]);
    }

    [Theory]
    [InlineData("2026-02-01,Rent,")]
    [InlineData("2026-02-01,Rent,   ")]
    public void Reports_a_row_whose_amount_is_blank(string row)
    {
        var result = _parser.Parse(
            $"Date,Description,Amount\n{row}\n",
            new ColumnMap(Date: "Date", Description: "Description", Amount: "Amount"));

        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldBe([new RowError(1, "amount is empty.")]);
    }

    [Theory]
    [InlineData("2026-02-01,Fee")]
    [InlineData("2026-02-01,Fee,")]
    [InlineData("2026-02-01,Fee,,")]
    [InlineData("2026-02-01,Fee,  ,  ")]
    public void Reports_a_row_with_neither_side_of_a_debit_and_credit_pair(string row)
    {
        var result = _parser.Parse(
            $"Date,Description,Debit,Credit\n{row}\n",
            new ColumnMap(Date: "Date", Description: "Description", Debit: "Debit", Credit: "Credit"));

        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldBe([new RowError(1, "amount is empty.")]);
    }

    [Fact]
    public void A_debit_and_credit_row_with_neither_side_does_not_cost_the_rows_around_it()
    {
        const string csv =
            "Date,Description,Debit,Credit\n" +
            "2026-02-01,Fee,25.00\n" +
            "2026-02-02,Nothing\n" +
            "2026-02-03,Interest,,1.25\n";

        var result = _parser.Parse(csv, new ColumnMap(
            Date: "Date", Description: "Description", Debit: "Debit", Credit: "Credit"));

        result.Rows.Select(r => r.Amount).ShouldBe([-25.00m, 1.25m]);
        result.Errors.ShouldBe([new RowError(2, "amount is empty.")]);
    }

    [Fact]
    public void Reports_a_blank_row_when_only_one_side_of_the_pair_is_mapped()
    {
        // The validator admits a map with a debit column alone, and the column it maps is then the row's amount.
        const string csv =
            "Date,Description,Debit\n" +
            "2026-02-01,Fee,25.00\n" +
            "2026-02-02,Nothing,\n";

        var result = _parser.Parse(csv, new ColumnMap(Date: "Date", Description: "Description", Debit: "Debit"));

        result.Rows.ShouldHaveSingleItem().Amount.ShouldBe(-25.00m);
        result.Errors.ShouldBe([new RowError(2, "amount is empty.")]);
    }

    public static TheoryData<string, string, decimal> RowsCarryingOneSide => new()
    {
        { "Date,Description,Debit,Credit", "2026-02-01,Fee,25.00", -25.00m }, // a bank omits the empty trailing credit
        { "Date,Description,Credit,Debit", "2026-02-01,Deposit,40.00", 40.00m }, // the same, the other way round
        { "Date,Description,Debit,Credit", "2026-02-01,Interest,,1.25", 1.25m },
        { "Date,Description,Debit,Credit", "2026-02-01,Stated zero,0.00,", 0.00m },
        { "Date,Description,Debit,Credit", "2026-02-01,Stated zero,,0.00", 0.00m },
        { "Date,Description,Debit,Credit", "2026-02-01,Stated zero,0.00,0.00", 0.00m },
    };

    [Theory]
    [MemberData(nameof(RowsCarryingOneSide))]
    public void A_row_carrying_one_side_of_a_debit_and_credit_pair_has_an_amount(string header, string row, decimal expected)
    {
        var result = _parser.Parse(
            $"{header}\n{row}\n",
            new ColumnMap(Date: "Date", Description: "Description", Debit: "Debit", Credit: "Credit"));

        result.Errors.ShouldBeEmpty();
        result.Rows.ShouldHaveSingleItem().Amount.ShouldBe(expected);
    }

    [Fact]
    public void A_stated_zero_amount_is_an_amount()
    {
        const string csv = "Date,Description,Amount\n2026-02-01,Stated zero,0.00\n";

        var result = _parser.Parse(csv, new ColumnMap(Date: "Date", Description: "Description", Amount: "Amount"));

        result.Errors.ShouldBeEmpty();
        result.Rows.ShouldHaveSingleItem().Amount.ShouldBe(0.00m);
    }
}
