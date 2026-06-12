namespace LeaseBook.Modules.Accounting.Persistence;

/// <summary>Tiny SQL helpers for building CHECK-constraint literals from the converter value sets.</summary>
internal static class AccountingSql
{
    /// <summary>Renders a value set as a single-quoted, comma-separated SQL list (e.g. <c>'a','b'</c>).</summary>
    public static string Quote(IEnumerable<string> values) =>
        string.Join(",", values.Select(v => $"'{v}'"));
}
