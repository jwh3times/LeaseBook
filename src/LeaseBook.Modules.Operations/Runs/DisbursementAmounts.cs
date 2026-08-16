namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// The canonical fee-then-reserve calculation shared by disbursement runs and read-only financial
/// summaries. Keeping the policy here prevents a dashboard from inventing a second definition of
/// money that is available to an owner.
/// </summary>
public static class DisbursementAmounts
{
    public static DisbursementAmountResult Compute(decimal equity, int? managementFeeBps, decimal reserve)
    {
        var fee = MgmtFee.Compute(equity, managementFeeBps);
        var netBeforeReserve = equity - fee;
        return new DisbursementAmountResult(fee, netBeforeReserve, netBeforeReserve - reserve);
    }
}

public sealed record DisbursementAmountResult(
    decimal Fee,
    decimal NetBeforeReserve,
    decimal AfterReserve)
{
    public decimal AvailableToDisburse => Math.Max(0m, AfterReserve);
}
