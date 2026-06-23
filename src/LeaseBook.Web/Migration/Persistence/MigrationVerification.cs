using LeaseBook.SharedKernel;

namespace LeaseBook.Web.Onboarding.Persistence;

/// <summary>
/// Immutable verification snapshot produced at sign-off time (M7 toolkit). Records the expected totals
/// (from the AppFolio export), the actual totals (from the posted opening journal entries), the variance,
/// and whether the import tied to $0. Once <see cref="SignedOffAt"/> is set the row is frozen.
/// </summary>
public sealed class MigrationVerification : IOrgScoped
{
    private MigrationVerification()
    {
        // EF parameterless constructor + factory below.
        ExpectedJson = null!;
        ActualJson = null!;
        ReportSnapshot = null!;
    }

    private MigrationVerification(
        DateOnly cutoverDate,
        string expectedJson,
        string actualJson,
        decimal varianceTotal,
        bool isTied,
        string? signedOffBy,
        DateTime? signedOffAt,
        string reportSnapshot)
    {
        Id = UuidV7.NewId();
        CutoverDate = cutoverDate;
        ExpectedJson = expectedJson;
        ActualJson = actualJson;
        VarianceTotal = varianceTotal;
        IsTied = isTied;
        SignedOffBy = signedOffBy;
        SignedOffAt = signedOffAt;
        ReportSnapshot = reportSnapshot;
    }

    public Guid Id { get; private set; }

    /// <inheritdoc />
    public Guid OrgId { get; set; }

    /// <summary>The cutover boundary date this verification covers.</summary>
    public DateOnly CutoverDate { get; private set; }

    /// <summary>JSON object of the expected totals from the AppFolio export.</summary>
    public string ExpectedJson { get; private set; }

    /// <summary>JSON object of the actual totals computed from the posted opening entries.</summary>
    public string ActualJson { get; private set; }

    /// <summary>The absolute sum of all per-account variances (0.00 means the import tied).</summary>
    public decimal VarianceTotal { get; private set; }

    /// <summary>True when the migration clearing account nets to $0.00 in both bases.</summary>
    public bool IsTied { get; private set; }

    /// <summary>The user identifier who signed off (null until sign-off).</summary>
    public string? SignedOffBy { get; private set; }

    /// <summary>When the sign-off occurred (null until sign-off).</summary>
    public DateTime? SignedOffAt { get; private set; }

    /// <summary>Rendered PDF/text snapshot of the verification report (immutable after sign-off).</summary>
    public string ReportSnapshot { get; private set; }

    public DateTime CreatedAt { get; private set; }

    internal static MigrationVerification Create(
        DateOnly cutoverDate,
        string expectedJson,
        string actualJson,
        decimal varianceTotal,
        bool isTied,
        string? signedOffBy,
        DateTime? signedOffAt,
        string reportSnapshot) =>
        new(cutoverDate, expectedJson, actualJson, varianceTotal, isTied, signedOffBy, signedOffAt, reportSnapshot);
}
