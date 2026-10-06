namespace LeaseBook.Modules.Accounting.Features.LedgerPosting;

/// <summary>
/// The source references of the entries one processor payout posts (ADR-053):
/// <c>payout:{reference}:{item}</c> for a receipt or a return and <c>payout:{reference}:{item}:fee</c>
/// for a fee difference. The shared <c>payout:{reference}:</c> prefix is what makes a payout's bank lines
/// one group: the register reports it, and a clearance takes the whole group or none of it. A payout
/// reference never contains a colon, so the reference reads back out of the source reference exactly.
/// </summary>
public static class PayoutSourceRef
{
    public const string Prefix = "payout:";

    private const char Separator = ':';

    public static bool IsValidReference(string? reference) =>
        !string.IsNullOrWhiteSpace(reference) && !reference.Contains(Separator, StringComparison.Ordinal);

    public static string For(string reference, string item) => $"{GroupPrefix(reference)}{item}";

    public static string ForFee(string reference, string item) => $"{For(reference, item)}:fee";

    /// <summary>The prefix every entry of the payout shares, ending in the separator.</summary>
    public static string GroupPrefix(string reference) => $"{Prefix}{reference}{Separator}";

    /// <summary>The payout reference an entry was posted under, or null when a payout did not post it.</summary>
    public static string? ReferenceOf(string? sourceRef)
    {
        if (sourceRef is null || !sourceRef.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var end = sourceRef.IndexOf(Separator, Prefix.Length);
        return end > Prefix.Length ? sourceRef[Prefix.Length..end] : null;
    }
}
