namespace LeaseBook.Modules.Operations.Contracts;

// ── Data DTO ─────────────────────────────────────────────────────────────────

/// <summary>
/// One delinquent lease row returned by <see cref="IDelinquencyData"/>. Carries all dimension
/// fields needed to construct a <see cref="LateFeeIntent"/> plus the balance and days-late
/// for preview display.
/// </summary>
public sealed record DelinquentLedgerRow(
    Guid LeaseId,
    Guid TenantId,
    Guid PropertyId,
    Guid OwnerId,
    Guid? UnitId,
    string TenantName,
    string UnitLabel,
    decimal Rent,
    decimal Balance,
    int DaysLate);

// ── Port ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Read-direction cross-module port (ADR-007 / WP-3). Operations declares the interface;
/// the host adapter (<c>DelinquencyDataAdapter</c>) implements it by dispatching
/// Accounting's <c>GetDelinquencyAging</c> (for per-tenant balance) and Directory's
/// <c>GetActiveLeaseSchedule</c> (for lease → tenant mapping) via
/// <see cref="LeaseBook.SharedKernel.Cqrs.ISender"/>, then joining them to produce per-lease rows.
/// <para>
/// A tenant with multiple active leases gets one row per lease (since the late fee is
/// charged per-lease, not per-tenant). The balance is the tenant's total receivable balance
/// as of <paramref name="asOf"/>, attributed to each of their leases equally (tenant-level
/// delinquency spread is a known simplification for Phase 1; corrected in Phase 3 when
/// per-lease GL accounts are introduced).
/// </para>
/// </summary>
public interface IDelinquencyData
{
    /// <summary>
    /// Returns leases with a positive receivable balance whose oldest outstanding charge is
    /// more than <paramref name="gracePeriodEndDate"/> days past due, joined to their schedule
    /// dimensions. Only tenants with a positive total balance surface.
    /// </summary>
    /// <param name="year">Period year (used to fetch the active lease schedule).</param>
    /// <param name="month">Period month.</param>
    /// <param name="asOf">Balance age measured from this date (typically the period's first day).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<DelinquentLedgerRow>> GetAsync(
        int year, int month, DateOnly asOf, CancellationToken ct);
}
