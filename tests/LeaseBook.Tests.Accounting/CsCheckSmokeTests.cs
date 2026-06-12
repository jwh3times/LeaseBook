using CsCheck;
using LeaseBook.SharedKernel;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-02: proves CsCheck (P29's property-testing framework) runs under xUnit v3 — the foundation the
/// invariant/property harness (WP-07) and the catalog balance property (WP-05) build on. The property
/// itself is real: Money addition is commutative over randomly generated two-decimal amounts.
/// </summary>
public sealed class CsCheckSmokeTests
{
    // Generate exact two-decimal money from integer cents so construction never trips the P28 gate.
    private static readonly Gen<Money> GenMoney =
        Gen.Int[-100_000_000, 100_000_000].Select(cents => new Money(cents / 100m));

    [Fact]
    public void Money_addition_is_commutative()
    {
        Gen.Select(GenMoney, GenMoney)
            .Sample(pair => (pair.Item1 + pair.Item2).Equals(pair.Item2 + pair.Item1));
    }
}
