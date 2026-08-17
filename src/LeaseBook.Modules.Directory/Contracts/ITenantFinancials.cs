namespace LeaseBook.Modules.Directory.Contracts;

/// <summary>
/// Consumer-owned read port (ADR-007 / P49) for the tenant financial figures the Accounting module owns.
/// Directory depends only on this abstraction; the <b>host</b> adapter delegates to the M1 ledger queries
/// via <c>ISender</c>. <b>Batch maps only</b> (tenant id → figure) — never a per-id read in a loop
/// (M2-E12). The basis rule + PM-income isolation stay inside Accounting, never re-implemented here.
/// </summary>
public interface ITenantFinancials
{
    /// <summary>Tenant id → net balance (receivable − unapplied prepayment, org-default basis).</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> BalancesAsync(CancellationToken ct);

    /// <summary>Tenant id → security deposit currently held (liability, never income until applied).</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> DepositsHeldAsync(CancellationToken ct);

    /// <summary>
    /// Tenant id → ledger-derived financial standing as of the stated date. Delinquency and unapplied
    /// credit are independent amounts and may both be positive.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, TenantFinancialStanding>> StandingAsync(DateOnly asOf, CancellationToken ct);
}

public sealed record TenantFinancialStanding(decimal DelinquentBalance, decimal UnappliedCredit);
