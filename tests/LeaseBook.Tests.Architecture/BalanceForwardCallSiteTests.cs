using System.Text.RegularExpressions;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// <c>IBalanceForward</c> deliberately exposes arbitrary opening-position lines to the host for
/// seed fixtures and migration imports. Product event flows must use Accounting's business-event
/// catalog instead, or they could bypass its posting-template guards (ADR-006).
/// </summary>
public sealed class BalanceForwardCallSiteTests
{
    private static readonly Regex UsesBalanceForward = new(
        @"\bIBalanceForward\b",
        RegexOptions.Compiled);

    private static readonly string[] AllowedConsumers =
    [
        Path.Combine("src", "LeaseBook.Web", "Onboarding", "BalanceImportService.cs"),
        Path.Combine("src", "LeaseBook.Web", "Seeding", "DemoJournalSeed.cs"),
        Path.Combine("src", "LeaseBook.Web", "Seeding", "LoadSeeder.cs"),
    ];

    [Fact]
    public void Only_seed_and_import_code_consume_balance_forward()
    {
        var offenders = RepositorySource.Current
            .CodeFilesUnder("src/LeaseBook.Web")
            .Where(file => !AllowedConsumers.Contains(file.RelativePath, StringComparer.Ordinal))
            .SelectMany(file => file.Find(UsesBalanceForward))
            .Select(match => match.ToString())
            .ToList();

        offenders.ShouldBeEmpty(
            "IBalanceForward accepts arbitrary opening-position lines and is restricted to seed/import " +
            "code; product money flows must use IAccountingEvents and the posting-template catalog" +
            (offenders.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, offenders)));
    }

    [Fact]
    public void Every_allowed_consumer_still_uses_balance_forward()
    {
        var staleAllowances = AllowedConsumers
            .Where(path => RepositorySource.Current.File(path).Find(UsesBalanceForward).Count == 0)
            .ToList();

        staleAllowances.ShouldBeEmpty(
            "remove stale allowlist entries so a same-path future feature cannot inherit permission to " +
            "consume IBalanceForward");
    }
}
