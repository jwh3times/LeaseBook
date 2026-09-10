using System.Collections.Frozen;

namespace LeaseBook.Modules.Accounting.Features.Statements;

/// <summary>Statement sections, in render order (Beginning/Ending are computed, not event-mapped).</summary>
public enum StatementSectionKey { Beginning, Income, OperatingExpenses, AppliedDepositsCredits, Contributions, Disbursement }

/// <summary>Thrown when an owner-equity-bearing event has no statement section — the guard that keeps
/// the §4.1 tie-out exhaustive (a new event type fails loudly instead of dropping off a statement).</summary>
public sealed class UncategorizedEventException(string eventType)
    : Exception("This statement contains an entry type that cannot be categorized yet.")
{
    public string EventType { get; } = eventType;
}

/// <summary>The single source of the event_type → section rule (ADR-006 catalog, owner-equity lines only).</summary>
public static class StatementSectionMap
{
    /// <summary>
    /// The rule itself. A dictionary rather than a switch expression so the domain is enumerable:
    /// invariant I8 sweeps the journal for owner-equity event types that are missing from it, and a
    /// second hand-maintained list would be free to disagree with the map it is supposed to guard.
    /// </summary>
    private static readonly FrozenDictionary<string, StatementSectionKey> Sections =
        new Dictionary<string, StatementSectionKey>(StringComparer.Ordinal)
        {
            ["RentCharged"] = StatementSectionKey.Income,
            ["FeeCharged"] = StatementSectionKey.Income,
            ["PaymentReceived"] = StatementSectionKey.Income,
            ["ManagementFeeAssessed"] = StatementSectionKey.OperatingExpenses,
            ["VendorPaid"] = StatementSectionKey.OperatingExpenses,
            ["DepositApplied"] = StatementSectionKey.AppliedDepositsCredits,
            ["PrepaymentApplied"] = StatementSectionKey.AppliedDepositsCredits,
            ["CreditIssued"] = StatementSectionKey.AppliedDepositsCredits,
            ["OwnerContribution"] = StatementSectionKey.Contributions,
            ["OwnerDisbursed"] = StatementSectionKey.Disbursement,
            // BalanceForward only ever appears before the period (folded into Beginning), never in-period.
            ["BalanceForward"] = StatementSectionKey.Beginning,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Every <c>event_type</c> a statement can account for on an owner-equity line — the mapped
    /// sections plus <c>OpeningBalance</c>, which is covered by exclusion rather than by a section:
    /// <c>GetOwnerStatementData</c> keeps opening-typed entries out of in-period movement and folds
    /// them into Beginning instead. Absence from this set is what invariant I8 reports.
    /// </summary>
    public static readonly IReadOnlySet<string> CoveredEventTypes =
        Sections.Keys.Append("OpeningBalance").ToFrozenSet(StringComparer.Ordinal);

    public static StatementSectionKey Section(string eventType) =>
        Sections.TryGetValue(eventType, out var key)
            ? key
            : throw new UncategorizedEventException(eventType);

    public static string Title(StatementSectionKey key) => key switch
    {
        StatementSectionKey.Income => "Income — rent collected",
        StatementSectionKey.OperatingExpenses => "Operating expenses",
        StatementSectionKey.AppliedDepositsCredits => "Applied deposits & credits",
        StatementSectionKey.Contributions => "Owner contributions",
        StatementSectionKey.Disbursement => "Owner disbursement",
        _ => "",
    };
}
