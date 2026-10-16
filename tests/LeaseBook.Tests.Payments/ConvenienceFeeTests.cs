using CsCheck;
using LeaseBook.Modules.Payments.Domain;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// The convenience-fee quote (ADR-053, #500): the charge is the smallest amount whose net, after the
/// processor's fee, is the amount going to the tenant's ledger. The rates are illustrative, not any
/// provider's pricing.
/// </summary>
public sealed class ConvenienceFeeTests
{
    private static readonly ConvenienceFeeRule Card = new(290, 0.30m, null);
    private static readonly ConvenienceFeeRule BankDebit = new(80, 0m, 5m);

    [Fact]
    public void The_worked_quotes_in_the_specification_hold()
    {
        Card.Quote(1000m).ShouldBe(new FeeQuote(1000m, 30.17m, 1030.17m));
        Card.Quote(500m).ShouldBe(new FeeQuote(500m, 15.24m, 515.24m));
        Card.Quote(250m).ShouldBe(new FeeQuote(250m, 7.78m, 257.78m));
        BankDebit.Quote(1000m).ShouldBe(new FeeQuote(1000m, 5m, 1005m));
        BankDebit.Quote(250m).ShouldBe(new FeeQuote(250m, 2.02m, 252.02m));
    }

    [Fact]
    public void A_rule_that_charges_nothing_quotes_the_ledger_amount()
    {
        ConvenienceFeeRule.None.Quote(125.50m).ShouldBe(new FeeQuote(125.50m, 0m, 125.50m));
    }

    [Fact]
    public void Every_quote_nets_exactly_the_ledger_amount_and_is_the_smallest_charge_that_does()
    {
        var rules =
            from rateBps in Gen.Int[0, 2_000]
            from fixedCents in Gen.Int[0, 500]
            from capCents in Gen.Int[0, 100_000]
            select new ConvenienceFeeRule(rateBps, fixedCents / 100m,
                // Below the fixed fee is not a rule settings accept; zero stands for "no cap".
                capCents < fixedCents || capCents == 0 ? null : capCents / 100m);
        var amounts = Gen.Int[1, 5_000_000].Select(cents => cents / 100m);

        Gen.Select(rules, amounts).Sample((rule, ledgerAmount) =>
        {
            var quote = rule.Quote(ledgerAmount);

            quote.LedgerAmount.ShouldBe(ledgerAmount);
            quote.Charged.ShouldBe(ledgerAmount + quote.Fee);
            quote.Fee.ShouldBe(rule.FeeOn(quote.Charged));
            (quote.Charged - rule.FeeOn(quote.Charged)).ShouldBe(ledgerAmount);
            if (rule.Cap is { } cap) { quote.Fee.ShouldBeLessThanOrEqualTo(cap); }
            // The net never falls as the charge rises, so one cent less netting short proves minimality.
            var oneCentLess = quote.Charged - 0.01m;
            (oneCentLess - rule.FeeOn(oneCentLess)).ShouldBeLessThan(ledgerAmount);
        }, iter: 20_000);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("10.005")]
    public void An_amount_that_is_not_positive_whole_cents_cannot_be_quoted(string amount)
    {
        var ledgerAmount = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        var refused = Should.Throw<ArgumentOutOfRangeException>(() => Card.Quote(ledgerAmount));
        // An unhandled refusal is logged by its message, so the message says what was wrong.
        (refused.ParamName, refused.ActualValue).ShouldBe(("ledgerAmount", (object)ledgerAmount));
        refused.Message.ShouldStartWith("A ledger amount is a positive amount in whole cents.");
    }

    [Fact]
    public void The_smallest_amount_that_can_be_quoted_is_one_cent()
    {
        Card.Quote(0.01m).ShouldBe(new FeeQuote(0.01m, 0.31m, 0.32m));
        ConvenienceFeeRule.None.Quote(0.01m).ShouldBe(new FeeQuote(0.01m, 0m, 0.01m));
    }

    public static TheoryData<string, ConvenienceFeeRule> Unusable => new()
    {
        { "a rate of all of the charge", new(10_000, 0m, null) },
        { "a negative rate", new(-1, 0m, null) },
        { "a negative fixed fee", new(100, -0.01m, null) },
        { "a cap of nothing", new(100, 0m, 0m) },
        { "a negative cap", new(100, 0m, -1m) },
    };

    [Theory]
    [MemberData(nameof(Unusable))]
    public void A_rule_with_no_gross_up_is_refused_rather_than_looped_on(string _, ConvenienceFeeRule rule)
    {
        Should.Throw<InvalidOperationException>(() => rule.Quote(100m))
            .Message.ShouldBe("The fee rule is not one a charge can be grossed up for.");
    }

    // Each limit from the side a rule may stand on: the highest rate below all of the charge, no rate,
    // no fixed fee, and the smallest cap.
    public static TheoryData<string, ConvenienceFeeRule, FeeQuote> AtTheLimits => new()
    {
        { "a rate a basis point short of all of it", new(9_999, 0m, null), new(0.01m, 50m, 50.01m) },
        { "no rate and no fixed fee", new(0, 0m, null), new(0.01m, 0m, 0.01m) },
        { "a cap of one cent", new(9_999, 0m, 0.01m), new(0.01m, 0.01m, 0.02m) },
    };

    [Theory]
    [MemberData(nameof(AtTheLimits))]
    public void A_rule_at_the_edge_of_what_can_be_grossed_up_is_quoted(string _, ConvenienceFeeRule rule, FeeQuote expected)
    {
        rule.Quote(0.01m).ShouldBe(expected);
    }
}
