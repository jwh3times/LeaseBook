namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// Pure static calculator for the NC §42-46 late-fee statutory clamp (WP-3).
/// <para>
/// <b>Raw fee:</b>
/// <list type="bullet">
///   <item><see cref="LateFeeKind.Flat"/> → <see cref="LateFeePolicy.FlatAmount"/>.</item>
///   <item><see cref="LateFeeKind.Percent"/> → <c>Round(rent × RateBps / 10000, 2, AwayFromZero)</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>NC §42-46 clamp:</b> <c>Min(raw, Max(15.00, Round(0.05 × rent, 2, AwayFromZero)))</c>.
/// The cap is the greater of $15 or 5 % of monthly rent, and the fee may not exceed it.
/// </para>
/// </summary>
public static class LateFeeCalculator
{
    /// <summary>
    /// Computes the late fee for the given <paramref name="policy"/> and <paramref name="monthlyRent"/>,
    /// applying the NC §42-46 statutory maximum cap.
    /// </summary>
    /// <param name="policy">The effective late-fee policy (org default + any lease override).</param>
    /// <param name="monthlyRent">The lease's monthly rent (must be &gt;= 0).</param>
    /// <returns>The clamped late fee, rounded to two decimal places.</returns>
    public static decimal Compute(LateFeePolicy policy, decimal monthlyRent)
    {
        // Raw fee: flat or percent-of-rent.
        var raw = policy.Kind switch
        {
            LateFeeKind.Flat => policy.FlatAmount,
            LateFeeKind.Percent => Math.Round(
                monthlyRent * policy.RateBps / 10000m, 2, MidpointRounding.AwayFromZero),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy.Kind, "Unknown LateFeeKind."),
        };

        // NC §42-46 cap: the greater of $15 or 5 % of monthly rent.
        var fivePercent = Math.Round(0.05m * monthlyRent, 2, MidpointRounding.AwayFromZero);
        var cap = Math.Max(15.00m, fivePercent);

        // Apply the cap — the fee may not exceed it.
        return Math.Min(raw, cap);
    }
}
