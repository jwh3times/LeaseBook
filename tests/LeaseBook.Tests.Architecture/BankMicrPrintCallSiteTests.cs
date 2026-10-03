using System.Text.RegularExpressions;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// <c>GetBankMicrPrint</c> hands back a bank account's full routing number and On-Us field, decrypted, for
/// the check renderer (#474, ADR-051). Every other read of those numbers carries only their last four digits,
/// so the query is dispatched from the refund-check print routes and nowhere else.
/// </summary>
public sealed class BankMicrPrintCallSiteTests
{
    private static readonly Regex DispatchesMicrPrint = new(@"\bnew\s+GetBankMicrPrint\s*\(", RegexOptions.Compiled);

    private static readonly string[] AllowedCallers =
    [
        Path.Combine("src", "LeaseBook.Web", "Payments", "RefundCheckEndpoints.cs"),
    ];

    [Fact]
    public void Only_the_refund_check_print_routes_read_the_full_micr_numbers()
    {
        var offenders = RepositorySource.Current
            .CodeFilesUnder("src")
            .Where(file => !AllowedCallers.Contains(file.RelativePath, StringComparer.Ordinal))
            .SelectMany(file => file.Find(DispatchesMicrPrint))
            .Select(match => match.ToString())
            .ToList();

        offenders.ShouldBeEmpty(
            "GetBankMicrPrint returns the full, decrypted MICR numbers and is restricted to the refund-check " +
            "print routes; anything else reads the masked GetBankMicrDetails view" +
            (offenders.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, offenders)));
    }

    [Fact]
    public void Every_allowed_caller_still_reads_them()
    {
        var staleAllowances = AllowedCallers
            .Where(path => RepositorySource.Current.File(path).Find(DispatchesMicrPrint).Count == 0)
            .ToList();

        staleAllowances.ShouldBeEmpty(
            "remove stale allowlist entries so a same-path future feature cannot inherit permission to read " +
            "the full MICR numbers");
    }
}
