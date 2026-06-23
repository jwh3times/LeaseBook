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
}
