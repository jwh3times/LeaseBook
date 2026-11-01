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

/// <summary>
/// A row the parser could not read: the 1-based file line where it starts, so the operator can find it, and the
/// reason. Reported, never fatal.
/// </summary>
public sealed record RowError(int LineNumber, string Message);

/// <summary>
/// A column the map names that the file's header row does not have: <see cref="Field"/> is the
/// <see cref="ColumnMap"/> property (<c>Date</c>, <c>Amount</c>, …), <see cref="Name"/> the header it named.
/// </summary>
public sealed record MissingColumn(string Field, string Name);

/// <summary>
/// The result of parsing one CSV: the rows it could read plus the rows it could not. A non-empty
/// <see cref="MissingColumns"/> means the map does not fit the file, and no row was read (#645).
/// </summary>
public sealed record ParsedStatement(
    IReadOnlyList<ParsedRow> Rows, IReadOnlyList<RowError> Errors, IReadOnlyList<MissingColumn> MissingColumns)
{
    public ParsedStatement(IReadOnlyList<ParsedRow> rows, IReadOnlyList<RowError> errors)
        : this(rows, errors, [])
    {
    }
}

/// <summary>
/// The statement-parser seam. CSV is the only implementation this milestone (P66); OFX/QFX slots in later
/// as another implementation without reworking the import pipeline.
/// </summary>
public interface IStatementParser
{
    ParsedStatement Parse(string content, ColumnMap map);

    /// <summary>
    /// The column names the file offers a <see cref="ColumnMap"/>, in file order: each is one
    /// <see cref="Parse"/> finds (#651). A file with no named column has none.
    /// </summary>
    IReadOnlyList<string> ReadHeaders(string content);
}
