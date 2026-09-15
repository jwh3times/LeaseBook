using LeaseBook.Migrator.Csv;

namespace LeaseBook.Web.Onboarding;

/// <summary>The terminal classification of one staged import row.</summary>
internal enum ImportOutcomeKind
{
    Posted,
    AlreadyPosted,
    Unchanged,
    Superseded,
    Skipped,
    Error,
}

internal sealed record ImportBatchSummary(
    int RowCount,
    int ErrorCount,
    string Status,
    ImportOutcomeCounts Counts);

internal static class ImportBatchMechanics
{
    /// <summary>
    /// Assigns each successfully parsed row its original one-based CSV data-row number. Parser
    /// errors already carry their source positions, so their positions are skipped rather than
    /// reused by a valid row.
    /// </summary>
    internal static IReadOnlyList<(TRow Row, int RowNumber)> AssignSourceRowNumbers<TRow>(
        IReadOnlyList<TRow> validRows,
        IReadOnlyList<RowError> parseErrors)
    {
        var errorRowNumbers = parseErrors.Select(error => error.RowNumber).ToHashSet();
        var numberedRows = new List<(TRow Row, int RowNumber)>(validRows.Count);
        var sourceRow = 0;

        foreach (var row in validRows)
        {
            do
            {
                sourceRow++;
            }
            while (errorRowNumbers.Contains(sourceRow));

            numberedRows.Add((row, sourceRow));
        }

        return numberedRows;
    }

    /// <summary>Derives the persisted batch status and response counts from immutable row outcomes.</summary>
    internal static ImportBatchSummary Summarize(IReadOnlyList<ImportOutcomeKind> outcomes)
    {
        var posted = 0;
        var alreadyPosted = 0;
        var unchanged = 0;
        var superseded = 0;
        var skipped = 0;
        var errors = 0;

        foreach (var outcome in outcomes)
        {
            switch (outcome)
            {
                case ImportOutcomeKind.Posted:
                    posted++;
                    break;
                case ImportOutcomeKind.AlreadyPosted:
                    alreadyPosted++;
                    break;
                case ImportOutcomeKind.Unchanged:
                    unchanged++;
                    break;
                case ImportOutcomeKind.Superseded:
                    superseded++;
                    break;
                case ImportOutcomeKind.Skipped:
                    skipped++;
                    break;
                case ImportOutcomeKind.Error:
                    errors++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown import outcome kind.");
            }
        }

        return new ImportBatchSummary(
            RowCount: outcomes.Count,
            ErrorCount: errors,
            Status: errors == 0 ? "posted" : "posted_with_errors",
            Counts: new ImportOutcomeCounts(posted, alreadyPosted, unchanged, superseded, skipped, errors));
    }
}
