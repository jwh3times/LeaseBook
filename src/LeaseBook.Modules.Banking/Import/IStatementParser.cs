namespace LeaseBook.Modules.Banking.Import;

/// <summary>
/// Maps the columns of an uploaded bank CSV to the normalized fields the importer needs (P66 / ADR-015).
/// Values are CSV header names. Provide either a single signed <see cref="Amount"/> column or a
/// <see cref="Debit"/>/<see cref="Credit"/> pair (money-out / money-in). <see cref="DateFormat"/> is an
/// optional explicit .NET date format; absent, the parser tries a small set of common bank formats.
/// </summary>
public sealed record ColumnMap(
    string Date,
    string Description,
    string? Amount = null,
    string? Debit = null,
    string? Credit = null,
    string? DateFormat = null);

/// <summary>A normalized statement row. <see cref="Amount"/> is signed: deposit +, withdrawal − (P67).</summary>
public sealed record ParsedRow(int RowNumber, DateOnly Date, string Description, decimal Amount);

/// <summary>A row the parser could not read (1-based data-row index + reason). Reported, never fatal.</summary>
public sealed record RowError(int RowNumber, string Message);

/// <summary>The result of parsing one CSV: the rows it could read plus the rows it could not.</summary>
public sealed record ParsedStatement(IReadOnlyList<ParsedRow> Rows, IReadOnlyList<RowError> Errors);

/// <summary>
/// The statement-parser seam. CSV is the only implementation this milestone (P66); OFX/QFX slots in later
/// as another implementation without reworking the import pipeline.
/// </summary>
public interface IStatementParser
{
    ParsedStatement Parse(string content, ColumnMap map);
}
