using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LeaseBook.Modules.Banking.Import;

/// <summary>
/// The re-import fingerprint (P67): a stable SHA-256 over <c>(statement_date, amount, normalized
/// description)</c>. Backs the <c>UNIQUE (org_id, bank_account_id, dedup_hash)</c> on
/// <c>statement_lines</c>, so a re-imported line collides with its prior copy and is skipped. Normalization
/// folds case and whitespace runs that banks vary between exports; date and amount are exact.
/// </summary>
public static partial class DedupHash
{
    public static string Compute(DateOnly date, decimal amount, string description)
    {
        var canonical = string.Create(CultureInfo.InvariantCulture,
            $"{date:yyyy-MM-dd}|{amount:0.00}|{Normalize(description)}");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }

    private static string Normalize(string? description) =>
        WhitespaceRuns().Replace((description ?? string.Empty).Trim(), " ").ToUpperInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRuns();
}
