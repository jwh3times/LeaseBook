namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// The effective late-fee policy for one lease: org-default fields overridden per-field by any
/// lease-level override columns (§WP-3 / task-3-brief). Constructed by the
/// <see cref="LeaseBook.Modules.Operations.Contracts.ILateFeePolicyData"/> host adapter after
/// resolving the override-or-default per field from Directory (ADR-007).
/// </summary>
/// <param name="RentDueDay">Day-of-month rent is due (1–28; default 1).</param>
/// <param name="GraceDays">Number of days after <see cref="RentDueDay"/> before the fee applies (default 5).</param>
/// <param name="Kind">Whether the fee is a flat dollar amount or a percentage of monthly rent.</param>
/// <param name="FlatAmount">Flat dollar amount (used when <see cref="Kind"/> is <see cref="LateFeeKind.Flat"/>).</param>
/// <param name="RateBps">Fee rate in basis points (100 bps = 1 %; used when <see cref="Kind"/> is <see cref="LateFeeKind.Percent"/>).</param>
public sealed record LateFeePolicy(
    int RentDueDay,
    int GraceDays,
    LateFeeKind Kind,
    decimal FlatAmount,
    int RateBps);
