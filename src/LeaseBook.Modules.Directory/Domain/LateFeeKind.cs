namespace LeaseBook.Modules.Directory.Domain;

/// <summary>
/// How the late fee is calculated: a flat dollar amount or a percentage of monthly rent.
/// Stored in <c>org_settings</c> (org default) and <c>lease_lites</c> (per-lease override)
/// as snake_case text (<c>"flat"</c> / <c>"percent"</c>); see <see cref="LateFeeKindConverter"/>.
/// The consumer-side mirror (for <c>LateFeeCalculator</c>) is
/// <c>LeaseBook.Modules.Operations.Runs.LateFeeKind</c>; the host adapter translates.
/// </summary>
public enum LateFeeKind
{
    Flat,
    Percent,
}

/// <summary>EF value converter for <see cref="LateFeeKind"/>; snake_case text storage.</summary>
public static class LateFeeKindConverter
{
    public const string Flat = "flat";
    public const string Percent = "percent";

    public static readonly string[] DbValues = [Flat, Percent];

    public static LateFeeKind FromDb(string value) => value switch
    {
        Flat => LateFeeKind.Flat,
        Percent => LateFeeKind.Percent,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown late_fee_kind value."),
    };

    public static string ToDb(LateFeeKind kind) => kind switch
    {
        LateFeeKind.Flat => Flat,
        LateFeeKind.Percent => Percent,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown LateFeeKind."),
    };
}
