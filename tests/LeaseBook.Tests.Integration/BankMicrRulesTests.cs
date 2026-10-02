using LeaseBook.Modules.Payments.Features.RefundChecks;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #474's pure rules for a bank account's MICR numbers. The valid routing numbers are real ones printed on
/// a bank's MICR specification sheet (docs/research/micr-e13b-refund-checks.md §4), not values computed
/// here; each invalid one changes a single digit, which the 3-7-1 check digit always catches.
/// </summary>
public sealed class BankMicrRulesTests
{
    [Theory]
    [InlineData("111000012")]
    [InlineData("540900071")]
    public void A_real_routing_number_passes(string routing) =>
        BankMicr.IsValidRoutingNumber(routing).ShouldBeTrue();

    [Theory]
    [InlineData("111000013")] // last digit
    [InlineData("211000012")] // first digit
    [InlineData("540900171")] // a middle digit
    [InlineData("11100001")]  // too short
    [InlineData("1110000120")] // too long
    [InlineData("11100001a")] // not a digit
    [InlineData("000000000")] // sums to zero, names no institution
    public void Anything_else_fails(string routing) =>
        BankMicr.IsValidRoutingNumber(routing).ShouldBeFalse();

    [Theory]
    [InlineData("123456789U", true)]
    [InlineData("12-3456 789U", true)]
    [InlineData("U1234", true)]
    [InlineData("123456789012345678", true)]
    [InlineData("1234567890123456789", false)] // 19 positions
    [InlineData("12U-", false)]                // three digits
    [InlineData("1234A", false)]               // not an E-13B character
    [InlineData("", false)]
    public void The_on_us_field_takes_only_what_the_micr_font_prints(string field, bool valid) =>
        BankMicr.IsValidOnUsField(field).ShouldBe(valid);

    [Fact]
    public void Printing_the_command_or_the_request_body_never_shows_the_numbers()
    {
        var command = new SaveBankMicrDetails(Guid.NewGuid(), "blank", "111000012", "123456789U", 1m, 2m);
        var body = new LeaseBook.Web.Payments.BankMicrDetailsBody("blank", "111000012", "123456789U", 1m, 2m);

        foreach (var text in new[] { command.ToString(), body.ToString() })
        {
            text.ShouldNotContain("111000012");
            text.ShouldNotContain("123456789");
            text.ShouldContain("blank"); // still useful in a log line
        }
    }
}
