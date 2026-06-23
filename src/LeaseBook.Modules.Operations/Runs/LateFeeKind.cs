namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// How the late fee is calculated: a flat dollar amount or a percentage of monthly rent.
/// Stored in the Directory module's <c>org_settings</c> and <c>lease_lites</c> tables as
/// snake_case text (<c>"flat"</c> / <c>"percent"</c>) and surfaced here via the
/// <see cref="ILateFeePolicyData"/> port + host adapter (ADR-007 / WP-3).
/// </summary>
public enum LateFeeKind
{
    Flat,
    Percent,
}
