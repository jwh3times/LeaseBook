using LeaseBook.SharedKernel;

namespace LeaseBook.Web.Onboarding.Persistence;

/// <summary>
/// Header row for one CSV import batch (M7 toolkit). Write-once (append-only): created when the batch
/// is submitted; never mutated. The per-row outcomes are <see cref="ImportRow"/> rows that FK to this.
/// Status tracks overall batch outcome; ErrorCount/RowCount summarise the row results.
/// </summary>
public sealed class ImportBatch : IOrgScoped
{
    private ImportBatch()
    {
        // EF parameterless constructor + factory below.
        EntityKind = null!;
        MappingProfile = null!;
        SourceFilename = null!;
        Status = null!;
    }

    private ImportBatch(
        string entityKind,
        string mappingProfile,
        string sourceFilename,
        int rowCount,
        int errorCount,
        string status,
        Guid? actor)
    {
        Id = UuidV7.NewId();
        EntityKind = entityKind;
        MappingProfile = mappingProfile;
        SourceFilename = sourceFilename;
        RowCount = rowCount;
        ErrorCount = errorCount;
        Status = status;
        Actor = actor;
    }

    public Guid Id { get; private set; }

    /// <inheritdoc />
    public Guid OrgId { get; set; }

    /// <summary>The kind of entity being imported (e.g. "owner_balance").</summary>
    public string EntityKind { get; private set; }

    /// <summary>The mapping profile identifier used to parse the CSV (e.g. "appfolio-default").</summary>
    public string MappingProfile { get; private set; }

    /// <summary>Original filename of the uploaded CSV.</summary>
    public string SourceFilename { get; private set; }

    /// <summary>Total rows in the source file (excluding header).</summary>
    public int RowCount { get; private set; }

    /// <summary>Number of rows that failed validation.</summary>
    public int ErrorCount { get; private set; }

    /// <summary>Batch lifecycle status (e.g. "pending", "posted", "superseded").</summary>
    public string Status { get; private set; }

    /// <summary>The user id of the operator who submitted this batch.</summary>
    public Guid? Actor { get; private set; }

    public DateTime CreatedAt { get; private set; }

    internal static ImportBatch Create(
        string entityKind,
        string mappingProfile,
        string sourceFilename,
        int rowCount,
        int errorCount,
        string status,
        Guid? actor) =>
        new(entityKind, mappingProfile, sourceFilename, rowCount, errorCount, status, actor);
}
