using System.Text;
using LeaseBook.Migrator.Csv;
using Shouldly;
using Xunit;

namespace LeaseBook.Tests.Migrator;

public sealed class CsvImporterTests
{
    private static Stream Csv(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    private sealed record OwnerRow(string Name, decimal Reserve);

    private static readonly ColumnMappingProfile OwnerProfile = new(
    [
        new("name", ["Owner Name", "Name"], Required: true),
        new("reserve", ["Reserve", "Reserve Amount"], Required: false),
    ]);

    [Fact]
    public void Maps_headers_via_profile_and_collects_valid_rows()
    {
        var result = CsvImporter.Read(
            Csv("Owner Name,Reserve\nHargrove Family Trust,500.00\n"),
            OwnerProfile,
            ctx => new OwnerRow(ctx.Cells["name"], decimal.Parse(ctx.Cells["reserve"])));

        result.Errors.ShouldBeEmpty();
        result.Rows.ShouldHaveSingleItem().Name.ShouldBe("Hargrove Family Trust");
        result.Rows[0].Reserve.ShouldBe(500.00m);
    }

    [Fact]
    public void Missing_required_column_reports_a_header_error_not_a_crash()
    {
        var result = CsvImporter.Read(
            Csv("Reserve\n500.00\n"), OwnerProfile, ctx => new OwnerRow(ctx.Cells["name"], 0m));

        result.Rows.ShouldBeEmpty();
        result.Errors.ShouldContain(e => e.Field == "name" && e.Reason.Contains("required column"));
    }

    [Fact]
    public void Duplicate_normalized_header_does_not_throw_and_still_loads()
    {
        ImportResult<OwnerRow> result = null!;
        Should.NotThrow(() => result = CsvImporter.Read(
            Csv("Name,NAME,Reserve\nHargrove Family Trust,dup,500.00\n"),
            OwnerProfile,
            ctx => new OwnerRow(ctx.Cells["name"], decimal.Parse(ctx.Cells["reserve"]))));

        result.Rows.ShouldHaveSingleItem().Name.ShouldBe("Hargrove Family Trust");
    }

    [Fact]
    public void One_bad_row_does_not_sink_the_batch()
    {
        var result = CsvImporter.Read(
            Csv("Owner Name,Reserve\nGood,1.00\nBad,notmoney\n"),
            OwnerProfile,
            ctx =>
            {
                if (!decimal.TryParse(ctx.Cells["reserve"], out var r))
                    return ctx.Reject<OwnerRow>("reserve", "not a number");
                return new OwnerRow(ctx.Cells["name"], r);
            });

        result.Rows.ShouldHaveSingleItem().Name.ShouldBe("Good");
        result.Errors.ShouldContain(e => e.RowNumber == 2 && e.Field == "reserve");
    }

    [Fact]
    public void A_row_with_malformed_quoting_is_reported_and_the_rows_after_it_keep_their_numbers()
    {
        // Recovering would piece a reserve together from the broken quotes ("1"2 reads as 12); the row is
        // reported instead, and the next row is still row 3.
        var result = CsvImporter.Read(
            Csv("Owner Name,Reserve\nGood,1.00\nBroken,\"1\"2\nAfter,3.00\n"),
            OwnerProfile,
            ctx => new OwnerRow(ctx.Cells["name"], decimal.Parse(ctx.Cells["reserve"])));

        result.Rows.Select(r => (r.Name, r.Reserve)).ShouldBe([("Good", 1.00m), ("After", 3.00m)]);
        var error = result.Errors.ShouldHaveSingleItem();
        error.RowNumber.ShouldBe(2);
        error.Reason.ShouldBe("row skipped: text after the closing quote of a value");
    }

    [Fact]
    public void Trims_spaces_around_a_value_but_keeps_tabs()
    {
        var result = CsvImporter.Read(
            Csv("Owner Name,Reserve\n  Hargrove Family Trust  ,\t500.00\n"),
            OwnerProfile,
            ctx => new OwnerRow(ctx.Cells["name"], 0m) is var row && ctx.Cells["reserve"] == "\t500.00" ? row : null);

        result.Rows.ShouldHaveSingleItem().Name.ShouldBe("Hargrove Family Trust");
    }

    [Fact]
    public void Reads_a_one_column_file_whose_values_contain_semicolons()
    {
        // CsvHelper guesses the separator from the first lines unless one is named, and guesses a semicolon
        // here, which would cut every owner name at its first semicolon.
        var result = CsvImporter.Read(
            Csv("Owner Name\nSmith; Jones\nLee; Park\n"),
            OwnerProfile,
            ctx => new OwnerRow(ctx.Cells["name"], 0m));

        result.Errors.ShouldBeEmpty();
        result.Rows.Select(r => r.Name).ShouldBe(["Smith; Jones", "Lee; Park"]);
    }
}
