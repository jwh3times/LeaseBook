namespace LeaseBook.Modules.Payments.Features.RefundChecks;

/// <summary>
/// The characters of a refund check's MICR line by position (#474, ADR-051). Positions count from the
/// right edge of the check, one per 0.125 in; position 1 is the right-most. Characters are the digits and
/// <see cref="Transit"/> (⑆), <see cref="OnUs"/> (⑈) and <see cref="Dash"/> (⑉). The amount symbol is
/// never printed: the amount field belongs to the bank of first deposit.
/// <para>
/// Layout, from the right: positions 1–13 blank (amount field and the gap after it); the bank's On-Us
/// field ending at position 14; position 32 blank; the transit field ⑆ + routing number + ⑆ at 33–43;
/// the External Processing Code (44/45) blank; and the check number in the Auxiliary On-Us field,
/// right-justified so its last digit is at position 48, between On-Us symbols. Sources and the evidence
/// gaps are in <c>docs/research/micr-e13b-refund-checks.md</c>.
/// </para>
/// </summary>
public sealed class MicrLine
{
    public const char Transit = 'T';
    public const char OnUs = 'U';
    public const char Dash = '-';

    private const int OnUsLastPosition = 14;
    private const int TransitRightPosition = 33;
    private const int AuxiliaryOnUsLastDigitPosition = 48;

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
        if (!BankMicr.IsValidRoutingNumber(routingNumber))
        {
            throw new ArgumentException("The routing number is not a valid ABA routing number.", nameof(routingNumber));
        }

        if (!BankMicr.IsValidOnUsField(onUsField))
        {
            throw new ArgumentException("The On-Us field holds characters the MICR font cannot print.", nameof(onUsField));
        }

        if (checkNumber <= 0)
        {
            throw new ArgumentException("A check number is required.", nameof(checkNumber));
        }

        var characters = new Dictionary<int, char>();
        PlaceRightAligned(characters, onUsField, OnUsLastPosition);
        PlaceRightAligned(characters, $"{Transit}{routingNumber}{Transit}", TransitRightPosition);
        var serial = checkNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PlaceRightAligned(characters, $"{OnUs}{serial}{OnUs}", AuxiliaryOnUsLastDigitPosition - 1);
        return new MicrLine(characters);
    }

    /// <summary>The line read left to right, from <see cref="HighestPosition"/> down to 1; blanks are spaces.</summary>
    public override string ToString() =>
        new(Enumerable.Range(1, HighestPosition).Reverse().Select(p => At(p) ?? ' ').ToArray());

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
