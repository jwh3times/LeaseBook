namespace LeaseBook.Modules.Payments.Domain;

/// <summary>
/// What a tenant is shown before confirming an online payment (ADR-053): the amount that goes toward
/// their ledger, the convenience fee on top, and the total charged.
/// </summary>
public sealed record FeeQuote(decimal LedgerAmount, decimal Fee, decimal Charged);

/// <summary>
/// A payment method's fee rule (ADR-053): a rate, a fixed amount and an optional cap, as a processor
/// publishes it. The fee is taken on the amount <b>charged</b>, so a quote has to gross the ledger
/// amount up: charge the smallest amount whose net, after the fee, is the ledger amount to the cent.
/// <para>
/// Pure and deterministic. The rule's values come from organization settings; nothing here reads them.
/// </para>
/// </summary>
public sealed record ConvenienceFeeRule(int RateBps, decimal FixedFee, decimal? Cap)
{
    /// <summary>Charges nothing: the charged amount is the ledger amount.</summary>
    public static readonly ConvenienceFeeRule None = new(0, 0m, null);

    /// <summary>The fee on an amount charged, rounded to the cent, half away from zero.</summary>
    public decimal FeeOn(decimal charged)
    {
        var fee = decimal.Round(charged * RateBps / 10_000m + FixedFee, 2, MidpointRounding.AwayFromZero);
        return Cap is { } cap ? Math.Min(fee, cap) : fee;
    }

    /// <summary>
    /// The quote for a ledger amount. Each extra cent charged raises the net by one cent or by nothing,
    /// so an exact charge always exists; this returns the smallest one.
    /// </summary>
    public FeeQuote Quote(decimal ledgerAmount)
    {
        if (ledgerAmount <= 0m || decimal.Round(ledgerAmount, 2) != ledgerAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(ledgerAmount), ledgerAmount,
                "A ledger amount is a positive amount in whole cents.");
        }
        if (RateBps is < 0 or >= 10_000 || FixedFee < 0m || Cap is <= 0m)
        {
            throw new InvalidOperationException("The fee rule is not one a charge can be grossed up for.");
        }

        // charged = ledgerAmount + FeeOn(charged). Starting from the ledger amount and re-applying the
        // fee climbs to the least amount that satisfies it: the fee never falls as the charge rises, so
        // every step is at or below the answer, and each step that is not the answer adds at least a cent.
        var charged = ledgerAmount;
        while (true)
        {
            var next = ledgerAmount + FeeOn(charged);
            if (next == charged) { return new FeeQuote(ledgerAmount, charged - ledgerAmount, charged); }
            charged = next;
        }
    }
}
