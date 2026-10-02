using LeaseBook.Modules.Payments.Domain;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #474 part 2: which E-13B character prints in which MICR position. Positions count from the right
/// edge of the check. The expected lines are worked by hand from the field rules, not by the code:
/// the amount field (1–12) and position 13 stay blank for the bank of first deposit; the On-Us field
/// ends at position 14 (X9 TR 100 def. 197; Fed X9.37 guide "positions 14 and 31"); the transit field
/// is ⑆ + nine digits + ⑆ at 33–43; the EPC (44/45) stays blank; and the check number sits in the
/// Auxiliary On-Us field, right-justified so its last digit is at position 48 (Fed X9.37 guide
/// "positions 48 - 62"), between On-Us symbols. In the strings, T is ⑆, U is ⑈ and - is ⑉.
/// </summary>
public sealed class MicrLineTests
{
    [Fact]
    public void A_business_check_carries_the_check_number_routing_number_and_on_us_field_in_their_positions()
    {
        var line = MicrLine.Compose(routingNumber: "111000012", onUsField: "123456789U", checkNumber: 1043);

        // Read left to right, position 52 down to position 1; a space is a blank position.
        var expected =
            "U1043U" +            // 52..47: Auxiliary On-Us — closing ⑈, the number ending at 48, opening ⑈
            "   " +               // 46..44: blank, EPC included
            "T111000012T" +       // 43..33: transit field
            " " +                 // 32: blank between fields
            "        " +          // 31..24: unused On-Us positions
            "123456789U" +        // 23..14: the bank's On-Us field, ending at 14
            new string(' ', 13);  // 13..1: blank, amount field included
        line.ToString().ShouldBe(expected);

        line.At(14).ShouldBe('U');
        line.At(33).ShouldBe('T');
        line.At(43).ShouldBe('T');
        line.At(48).ShouldBe('3');
        line.At(1).ShouldBeNull();
        line.HighestPosition.ShouldBe(52);
    }

    [Fact]
    public void Dashes_spaces_and_a_longer_check_number_keep_their_places()
    {
        var line = MicrLine.Compose("540900071", "U12-3456 789U", 12345678);

        line.ToString().ShouldBe(
            "U12345678U" +        // 56..47
            "   " +               // 46..44
            "T540900071T" +       // 43..33
            " " +                 // 32
            "     " +             // 31..27
            "U12-3456 789U" +     // 26..14, the dash and the empty position kept
            new string(' ', 13)); // 13..1
    }

    [Theory]
    [InlineData("111000013", "123456789U", 1043)] // routing check digit wrong
    [InlineData("111000012", "1234A", 1043)]      // not an E-13B character
    [InlineData("111000012", "123456789U", 0)]    // no check number
    public void Anything_the_rules_refuse_cannot_be_composed(string routing, string onUs, int checkNumber) =>
        Should.Throw<ArgumentException>(() => MicrLine.Compose(routing, onUs, checkNumber));
}
