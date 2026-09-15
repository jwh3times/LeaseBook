using LeaseBook.Migrator.Csv;
using LeaseBook.Web.Onboarding;
using Shouldly;

namespace LeaseBook.Tests.Web;

public sealed class ImportSourceRowsTests
{
    [Fact]
    public void AssignNumbers_preserves_positions_around_parse_errors()
    {
        var validRows = new[] { "first", "third", "fifth" };
        var parseErrors = new[]
        {
            new RowError(2, "name", "required"),
            new RowError(4, "name", "required"),
        };

        var numberedRows = ImportSourceRows.AssignNumbers(validRows, parseErrors);

        numberedRows.ShouldBe(
        [
            ("first", 1),
            ("third", 3),
            ("fifth", 5),
        ]);
    }
}
