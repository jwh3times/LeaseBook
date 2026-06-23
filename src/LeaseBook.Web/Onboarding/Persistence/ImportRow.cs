using LeaseBook.SharedKernel;

namespace LeaseBook.Web.Onboarding.Persistence;

/// <summary>
/// One parsed CSV row within an <see cref="ImportBatch"/>. Write-once (append-only): created at staging
/// time and never mutated. <see cref="RawJson"/> holds the original row values; <see cref="MappedJson"/>
/// holds the canonical mapped field values; <see cref="ErrorsJson"/> holds any validation errors (null
/// for valid rows). <see cref="ResultingJournalEntryId"/> is set when the row has been posted as an
/// opening-balance entry.
/// </summary>
public sealed class ImportRow : IOrgScoped
{
    private ImportRow()
    {
        // EF parameterless constructor + factory below.
        RawJson = null!;
        MappedJson = null!;
        RowStatus = null!;
    }

    private ImportRow(
        Guid batchId,
        int rowNumber,
        string rawJson,
        string mappedJson,
        string rowStatus,
        string? errorsJson,
        Guid? resultingJournalEntryId)
    {
        Id = UuidV7.NewId();
        BatchId = batchId;
        RowNumber = rowNumber;
        RawJson = rawJson;
        MappedJson = mappedJson;
        RowStatus = rowStatus;
        ErrorsJson = errorsJson;
        ResultingJournalEntryId = resultingJournalEntryId;
    }

    public Guid Id { get; private set; }

    /// <inheritdoc />
    public Guid OrgId { get; set; }

    /// <summary>The parent batch id (FK → <c>import_batches.id</c>).</summary>
    public Guid BatchId { get; private set; }

    /// <summary>1-based row number in the source CSV (excluding header).</summary>
    public int RowNumber { get; private set; }

    /// <summary>JSON object of the raw CSV values, keyed by column header.</summary>
    public string RawJson { get; private set; }

    /// <summary>JSON object of the canonical mapped field values after profile mapping.</summary>
    public string MappedJson { get; private set; }

    /// <summary>Row-level status (e.g. "valid", "error", "posted", "skipped").</summary>
    public string RowStatus { get; private set; }

    /// <summary>JSON array of validation error messages; null for valid rows.</summary>
    public string? ErrorsJson { get; private set; }

    /// <summary>The journal entry id created when this row was posted as an opening-balance entry.</summary>
    public Guid? ResultingJournalEntryId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    internal static ImportRow Create(
        Guid batchId,
        int rowNumber,
        string rawJson,
        string mappedJson,
        string rowStatus,
        string? errorsJson,
        Guid? resultingJournalEntryId = null) =>
        new(batchId, rowNumber, rawJson, mappedJson, rowStatus, errorsJson, resultingJournalEntryId);
}
