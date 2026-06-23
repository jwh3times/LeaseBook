namespace LeaseBook.Modules.Accounting.Features.Statements;

/// <summary>Statement sections, in render order (Beginning/Ending are computed, not event-mapped).</summary>
public enum StatementSectionKey { Beginning, Income, OperatingExpenses, AppliedDepositsCredits, Contributions, Disbursement }

/// <summary>Thrown when an owner-equity-bearing event has no statement section — the guard that keeps
/// the §4.1 tie-out exhaustive (a new event type fails loudly instead of dropping off a statement).</summary>
public sealed class UncategorizedEventException(string eventType)
    : Exception($"Event '{eventType}' posts to owner equity but has no statement section. Add it to StatementSectionMap.");

/// <summary>The single source of the event_type → section rule (ADR-006 catalog, owner-equity lines only).</summary>
public static class StatementSectionMap
{
    public static StatementSectionKey Section(string eventType) => eventType switch
    {
        "RentCharged" or "FeeCharged" or "PaymentReceived" => StatementSectionKey.Income,
        "ManagementFeeAssessed" or "VendorPaid" => StatementSectionKey.OperatingExpenses,
        "DepositApplied" or "PrepaymentApplied" or "CreditIssued" => StatementSectionKey.AppliedDepositsCredits,
        "OwnerContribution" => StatementSectionKey.Contributions,
        "OwnerDisbursed" => StatementSectionKey.Disbursement,
        // BalanceForward only ever appears before the period (folded into Beginning), never in-period.
        "BalanceForward" => StatementSectionKey.Beginning,
        _ => throw new UncategorizedEventException(eventType),
    };

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
