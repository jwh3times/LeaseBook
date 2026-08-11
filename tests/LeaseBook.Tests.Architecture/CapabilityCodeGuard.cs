using System.Text.RegularExpressions;

namespace LeaseBook.Tests.Architecture;

/// <summary>The one compiled-code vocabulary for references to the capability seam.</summary>
internal static class CapabilityCodeGuard
{
    private static readonly Regex Mention = new(
        @"Capabilit|Entitlement|feature_flag|IsEnabled\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<CompiledReference> FindMentions(IEnumerable<CompiledReference> references) =>
        [.. references.Where(reference => Mention.IsMatch(reference.Target))];
}
