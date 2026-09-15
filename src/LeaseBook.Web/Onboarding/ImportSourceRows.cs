using LeaseBook.Migrator.Csv;

namespace LeaseBook.Web.Onboarding;

internal static class ImportSourceRows
{
    /// <summary>
    /// Assigns each successfully parsed row its original one-based CSV data-row number. Parser
    /// errors already carry their source positions, so their positions are skipped rather than
    /// reused by a valid row.
    /// </summary>
    internal static IReadOnlyList<(TRow Row, int RowNumber)> AssignNumbers<TRow>(
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
}
