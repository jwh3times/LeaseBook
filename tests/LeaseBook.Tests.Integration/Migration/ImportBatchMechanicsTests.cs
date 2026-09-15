using LeaseBook.Migrator.Csv;
using LeaseBook.Web.Onboarding;
using Shouldly;

namespace LeaseBook.Tests.Integration.Migration;

public sealed class ImportBatchMechanicsTests
{
    [Fact]
    public void AssignSourceRowNumbers_preserves_positions_around_parse_errors()
    {
        var validRows = new[] { "first", "third", "fifth" };
        var parseErrors = new[]
        {
            new RowError(2, "name", "required"),
            new RowError(4, "name", "required"),
        };

        var numberedRows = ImportBatchMechanics.AssignSourceRowNumbers(validRows, parseErrors);

        numberedRows.ShouldBe(
        [
            ("first", 1),
            ("third", 3),
            ("fifth", 5),
        ]);
    }

    [Fact]
    public void Summarize_counts_outcomes_and_sets_the_error_status()
    {
        var outcomes = new[]
        {
            ImportOutcomeKind.Posted,
            ImportOutcomeKind.Posted,
            ImportOutcomeKind.AlreadyPosted,
            ImportOutcomeKind.Unchanged,
            ImportOutcomeKind.Superseded,
            ImportOutcomeKind.Skipped,
            ImportOutcomeKind.Error,
        };

        var summary = ImportBatchMechanics.Summarize(outcomes);

        summary.ShouldBe(new ImportBatchSummary(
            RowCount: 7,
            ErrorCount: 1,
            Status: "posted_with_errors",
            Counts: new ImportOutcomeCounts(
                Posted: 2,
                AlreadyPosted: 1,
                Unchanged: 1,
                Superseded: 1,
                Skipped: 1,
                Errors: 1)));
    }

    [Fact]
    public void Summarize_sets_the_posted_status_when_no_rows_failed()
    {
        var summary = ImportBatchMechanics.Summarize(
        [
            ImportOutcomeKind.Posted,
            ImportOutcomeKind.AlreadyPosted,
            ImportOutcomeKind.Skipped,
        ]);

        summary.ShouldBe(new ImportBatchSummary(
            RowCount: 3,
            ErrorCount: 0,
            Status: "posted",
            Counts: new ImportOutcomeCounts(
                Posted: 1,
                AlreadyPosted: 1,
                Unchanged: 0,
                Superseded: 0,
                Skipped: 1,
                Errors: 0)));
    }
}
