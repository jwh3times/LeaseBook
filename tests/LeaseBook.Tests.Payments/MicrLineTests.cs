using LeaseBook.Modules.Payments.Domain;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// #474 part 2: which E-13B character prints in which MICR position. Positions count from the right
/// edge of the check. The expected lines are worked by hand from the field rules, not by the code:
/// the amount field (1–12) and position 13 stay blank for the bank of first deposit; the On-Us field
/// ends at position 14 (X9 TR 100 def. 197; Fed X9.37 guide "positions 14 and 31"); the transit field
/// is ⑆ + nine digits + ⑆ at 33–43; the EPC (44/45) stays blank; and the check number sits in the
/// Auxiliary On-Us field between On-Us symbols, the closing symbol at 46 so that two blank positions
/// separate it from the transit field — the most Standard 006 §4.5 allows (ADR-051, amended in part 3;
/// the Fed X9.37 guide's "positions 48 - 62" would leave three). In the strings, T is ⑆, U is ⑈ and - is ⑉.
/// </summary>
public sealed class MicrLineTests
{
    [Fact]
    public void A_business_check_carries_the_check_number_routing_number_and_on_us_field_in_their_positions()
    {
        var line = MicrLine.Compose(routingNumber: "111000012", onUsField: "123456789U", checkNumber: 1043);

        // Read left to right, position 52 down to position 1; a space is a blank position.
        var expected =
            "U1043U" +            // 51..46: Auxiliary On-Us — opening ⑈, the number ending at 47, closing ⑈
            "  " +                // 45..44: blank, the EPC
            "T111000012T" +       // 43..33: transit field
            " " +                 // 32: blank between fields
            "        " +          // 31..24: unused On-Us positions
            "123456789U" +        // 23..14: the bank's On-Us field, ending at 14
            new string(' ', 13);  // 13..1: blank, amount field included
        line.Read().ShouldBe(expected);

        line.At(14).ShouldBe('U');
        line.At(33).ShouldBe('T');
        line.At(43).ShouldBe('T');
        line.At(46).ShouldBe('U');
        line.At(47).ShouldBe('3');
        line.At(44).ShouldBeNull();
        line.At(45).ShouldBeNull();
        line.At(1).ShouldBeNull();
        line.HighestPosition.ShouldBe(51);
    }

    [Fact]
    public void Dashes_spaces_and_a_longer_check_number_keep_their_places()
    {
        var line = MicrLine.Compose("540900071", "U12-3456 789U", 12345678);

        line.Read().ShouldBe(
            "U12345678U" +        // 55..46
            "  " +                // 45..44
            "T540900071T" +       // 43..33
            " " +                 // 32
            "     " +             // 31..27
            "U12-3456 789U" +     // 26..14, the dash and the empty position kept
            new string(' ', 13)); // 13..1
    }

    [Fact]
    public void A_specimen_carries_the_account_s_numbers_and_an_all_zero_serial()
    {
        // The alignment page on blank stock doubles as the bank's test sample: the real transit and On-Us
        // fields, and a serial no check can ever carry.
        MicrLine.Specimen("111000012", "123456789U").Read().ShouldBe(
            "U0000U" +            // 51..46
            "  " +                // 45..44
            "T111000012T" +       // 43..33
            " " +                 // 32
            "        " +          // 31..24
            "123456789U" +        // 23..14
            new string(' ', 13)); // 13..1
    }

    [Fact]
    public void The_characters_are_listed_from_the_right_edge()
    {
        // The renderer draws each character at its position, so the list must start at position 14, the
        // On-Us field's last character, and end with the Auxiliary On-Us field's opening symbol at 51.
        var characters = MicrLine.Compose("111000012", "123456789U", 1043).Characters.ToList();

        characters.Select(c => c.Position).ShouldBe(characters.Select(c => c.Position).Order());
        characters[0].ShouldBe((14, 'U'));
        characters[^1].ShouldBe((51, 'U'));
        characters.Count.ShouldBe(10 + 11 + 6);
    }

    [Fact]
    public void Printing_a_line_shows_how_long_it_is_and_never_its_numbers()
    {
        var line = MicrLine.Compose("111000012", "123456789U", 1043);

        line.ToString().ShouldBe("MicrLine { Positions = 51 }");
    }

    [Theory]
    [InlineData("111000013", "123456789U", 1043, "routingNumber", "The routing number is not a valid ABA routing number.")]
    [InlineData("111000012", "1234A", 1043, "onUsField", "The On-Us field holds characters the MICR font cannot print.")]
    [InlineData("111000012", "123456789U", 0, "checkNumber", "A check number is required.")]
    [InlineData("111000012", "123456789U", -1043, "checkNumber", "A check number is required.")]
    public void Anything_the_rules_refuse_cannot_be_composed(string routing, string onUs, int checkNumber, string param,
        string reason)
    {
        // The message is what the host logs when a print fails, so it names the field that was wrong.
        var refused = Should.Throw<ArgumentException>(() => MicrLine.Compose(routing, onUs, checkNumber));

        refused.ParamName.ShouldBe(param);
        refused.Message.ShouldStartWith(reason);
    }

    [Theory]
    [InlineData("111000013", "123456789U", "routingNumber")]
    [InlineData("111000012", "12U-", "onUsField")]
    public void A_specimen_is_held_to_the_same_rules(string routing, string onUs, string param) =>
        Should.Throw<ArgumentException>(() => MicrLine.Specimen(routing, onUs)).ParamName.ShouldBe(param);
}
