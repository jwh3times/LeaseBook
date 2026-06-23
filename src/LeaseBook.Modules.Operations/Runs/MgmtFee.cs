namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// Management-fee computation (ADR-018). Always decimal; rounds half away from zero.
/// Pure static: no dependencies, no DB access — unit-testable in isolation.
/// </summary>
public static class MgmtFee
{
    /// <summary>
    /// <c>fee = Round(equity × bps / 10000, 2, AwayFromZero)</c>.
    /// Returns 0 when <paramref name="bps"/> is null or 0.
    /// </summary>
    public static decimal Compute(decimal equityAtRunTime, int? bps)
    {
        if (bps is null or 0) return 0m;
        return Math.Round(equityAtRunTime * bps.Value / 10000m, 2, MidpointRounding.AwayFromZero);
    }
}
