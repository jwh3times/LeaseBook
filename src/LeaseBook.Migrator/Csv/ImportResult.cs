namespace LeaseBook.Migrator.Csv;

public sealed record ImportResult<TRow>(IReadOnlyList<TRow> Rows, IReadOnlyList<RowError> Errors);
