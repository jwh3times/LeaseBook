using System.Text.RegularExpressions;

namespace LeaseBook.Modules.Payments.Domain;

/// <summary>
/// The characters of a refund check's MICR line by position (#474, ADR-051). Positions count from the
/// right edge of the check, one per 0.125 in; position 1 is the right-most. Characters are the digits and
/// <see cref="Transit"/> (⑆), <see cref="OnUs"/> (⑈) and <see cref="Dash"/> (⑉). The amount symbol is
/// never printed: the amount field belongs to the bank of first deposit.
/// <para>
/// Layout, from the right: positions 1–13 blank (amount field and the gap after it); the bank's On-Us
/// field ending at position 14; position 32 blank; the transit field ⑆ + routing number + ⑆ at 33–43;
/// the External Processing Code (44/45) blank; and the check number in the Auxiliary On-Us field between
/// On-Us symbols, the closing one at position 46, so two blank positions separate it from the transit field
/// — the most Standard 006 §4.5 allows. Sources and the evidence gaps are in
/// <c>docs/research/micr-e13b-refund-checks.md</c>.
/// </para>
/// </summary>
public sealed partial class MicrLine
{
    public const char Transit = 'T';
    public const char OnUs = 'U';
    public const char Dash = '-';

    private const int OnUsLastPosition = 14;
    private const int TransitRightPosition = 33;
    private const int AuxiliaryOnUsClosingPosition = 46;

    private readonly Dictionary<int, char> _characters;

    private MicrLine(Dictionary<int, char> characters)
    {
        _characters = characters;
        HighestPosition = characters.Keys.Max();
    }

    /// <summary>The left-most occupied position.</summary>
    public int HighestPosition { get; }

    /// <summary>Every occupied position and its character, right-most first.</summary>
    public IEnumerable<(int Position, char Character)> Characters =>
        _characters.OrderBy(c => c.Key).Select(c => (c.Key, c.Value));

    /// <summary>The character at <paramref name="position"/>, or null where the line is blank.</summary>
    public char? At(int position) => _characters.TryGetValue(position, out var c) ? c : null;

    public static MicrLine Compose(string routingNumber, string onUsField, int checkNumber)
    {
        if (checkNumber <= 0)
        {
            throw new ArgumentException("A check number is required.", nameof(checkNumber));
        }

        return Build(routingNumber, onUsField, checkNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The alignment page's line on blank stock: the account's real transit and On-Us fields, so it can
    /// serve as the bank's test sample, and an all-zero serial that no check can carry.
    /// </summary>
    public static MicrLine Specimen(string routingNumber, string onUsField) => Build(routingNumber, onUsField, "0000");

    private static MicrLine Build(string routingNumber, string onUsField, string serial)
    {
        if (!IsValidRoutingNumber(routingNumber))
        {
            throw new ArgumentException("The routing number is not a valid ABA routing number.", nameof(routingNumber));
        }

        if (!IsValidOnUsField(onUsField))
        {
            throw new ArgumentException("The On-Us field holds characters the MICR font cannot print.", nameof(onUsField));
        }

        var characters = new Dictionary<int, char>();
        PlaceRightAligned(characters, onUsField, OnUsLastPosition);
        PlaceRightAligned(characters, $"{Transit}{routingNumber}{Transit}", TransitRightPosition);
        PlaceRightAligned(characters, $"{OnUs}{serial}{OnUs}", AuxiliaryOnUsClosingPosition);
        return new MicrLine(characters);
    }

    /// <summary>
    /// An ABA routing number: nine digits whose weighted sum (3, 7, 1 repeating) is a multiple of ten.
    /// All zeros passes the arithmetic but names no institution, so it is refused.
    /// </summary>
    public static bool IsValidRoutingNumber(string value)
    {
        if (value.Length != 9 || !value.All(char.IsAsciiDigit) || value == "000000000")
        {
            return false;
        }

        int[] weights = [3, 7, 1, 3, 7, 1, 3, 7, 1];
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (value[i] - '0') * weights[i];
        }

        return sum % 10 == 0;
    }

    /// <summary>
    /// The On-Us field as the bank's specification sheet prints it, left to right: digits,
    /// <see cref="Dash"/> for the dash symbol, a space for an empty position and <see cref="OnUs"/> for the
    /// On-Us symbol; at most 18 characters (positions 14–31) and at least four digits.
    /// </summary>
    public static bool IsValidOnUsField(string value) =>
        OnUsField().IsMatch(value) && value.Count(char.IsAsciiDigit) >= 4;

    [GeneratedRegex("^[0-9U\\- ]{1,18}$")]
    private static partial Regex OnUsField();

    /// <summary>
    /// The line read left to right, from <see cref="HighestPosition"/> down to 1; blanks are spaces. It holds
    /// the full routing number and On-Us field, so it is never what <see cref="ToString"/> returns.
    /// </summary>
    public string Read() =>
        new(Enumerable.Range(1, HighestPosition).Reverse().Select(p => At(p) ?? ' ').ToArray());

    /// <summary>A log line or an exception message that interpolates a line shows that it exists, never its numbers.</summary>
    public override string ToString() => $"{nameof(MicrLine)} {{ Positions = {HighestPosition} }}";

    /// <summary>Places <paramref name="text"/> so its right-most character lands on <paramref name="rightPosition"/>.</summary>
    private static void PlaceRightAligned(Dictionary<int, char> characters, string text, int rightPosition)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[text.Length - 1 - i];
            if (c != ' ')
            {
                characters[rightPosition + i] = c;
            }
        }
    }
}
