using LeaseBook.Web.Payments;
using Shouldly;

namespace LeaseBook.Tests.Web;

/// <summary>#473: the legal amount line printed on a refund check.</summary>
public sealed class CheckAmountWordsTests
{
    [Theory]
    [InlineData("0.45", "Zero and 45/100")]
    [InlineData("1", "One and 00/100")]
    [InlineData("15.5", "Fifteen and 50/100")]
    [InlineData("40", "Forty and 00/100")]
    [InlineData("75.00", "Seventy-five and 00/100")]
    [InlineData("100", "One hundred and 00/100")]
    [InlineData("422.58", "Four hundred twenty-two and 58/100")]
    [InlineData("1450.00", "One thousand four hundred fifty and 00/100")]
    [InlineData("1000000", "One million and 00/100")]
    [InlineData("2005010.99", "Two million five thousand ten and 99/100")]
    [InlineData("999999999.99", "Nine hundred ninety-nine million nine hundred ninety-nine thousand nine hundred ninety-nine and 99/100")]
    public void Formats_dollars_in_words_and_cents_as_a_fraction(string amount, string expected) =>
        CheckAmountWords.Format(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(expected);

    [Theory]
    [InlineData("-1")]
    [InlineData("1000000000")]
    [InlineData("1.005")]
    public void Rejects_amounts_a_check_cannot_carry(string amount) =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            CheckAmountWords.Format(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));
}
