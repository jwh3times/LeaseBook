namespace LeaseBook.Migrator.Csv;

/// <summary>One field-level problem on one CSV row (1-based row number, header excluded).</summary>
public sealed record RowError(int RowNumber, string Field, string Reason);
