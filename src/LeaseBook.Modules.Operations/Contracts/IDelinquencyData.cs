namespace LeaseBook.Modules.Operations.Contracts;

// ── Data DTO ─────────────────────────────────────────────────────────────────

/// <summary>
/// Whether a tenant-level delinquent balance can be attributed to a specific lease. The cases are
/// nested so callers cannot add a third meaning without changing this contract and every exhaustive
/// consumer.
/// </summary>
public abstract record DelinquencyAttribution
{
    private DelinquencyAttribution()
    {
    }

    /// <summary>A balance attributable to this lease and its specific rent obligation.</summary>
    public sealed record AttributedToLease : DelinquencyAttribution
    {
        public AttributedToLease(Guid rentObligationEntryId)
        {
            ArgumentOutOfRangeException.ThrowIfEqual(rentObligationEntryId, Guid.Empty);
            RentObligationEntryId = rentObligationEntryId;
        }

        public Guid RentObligationEntryId { get; }
    }

    /// <summary>
    /// A tenant has multiple active leases, so their tenant-level balance cannot be assigned to one.
    /// </summary>
    public sealed record AmbiguousMultipleActiveLeases : DelinquencyAttribution;

    /// <summary>The tenant owes money, but no canonical rent charge exists for this lease period.</summary>
    public sealed record NoRentObligation : DelinquencyAttribution;
}

/// <summary>
/// One delinquent lease row returned by <see cref="IDelinquencyData"/>. Carries all dimension
/// fields needed to construct a <see cref="LateFeeIntent"/> plus the balance-attribution result used
/// by the grace gate and preview display.
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
    /// <summary>
    /// Either the specific rent obligation, or an explicit reason that the tenant-level balance
    /// cannot safely be linked to one.
    /// </summary>
    DelinquencyAttribution Attribution);

// ── Port ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Read-direction cross-module port (ADR-007 / WP-3). Operations declares the interface;
/// the host adapter (<c>DelinquencyDataAdapter</c>) implements it by dispatching
/// Accounting's <c>GetDelinquencyAging</c> (for per-tenant balance), Accounting's
/// <c>GetRentObligationEntries</c> (for the specific canonical rent charge), and Directory's
/// <c>GetActiveLeaseSchedule</c> (for lease → tenant mapping) via
/// <see cref="LeaseBook.SharedKernel.Cqrs.ISender"/>, then joining them to produce per-lease rows.
/// <para>
/// A tenant with multiple active leases gets one row per lease (since the late fee is
/// charged per-lease, not per-tenant), each carrying an explicit
/// <see cref="DelinquencyAttribution.AmbiguousMultipleActiveLeases"/> case. No lease is chargeable
/// until per-lease GL accounts make the tenant-level balance attributable.
/// </para>
/// </summary>
public interface IDelinquencyData
{
    /// <summary>
    /// Returns leases with a positive receivable balance, joined to their schedule dimensions.
    /// Only tenants with a positive total balance surface. The grace-period gate is NOT applied
    /// here — callers calculate eligibility from the lease policy's contractual due date.
    /// </summary>
    /// <param name="year">Period year (used to fetch the active lease schedule).</param>
    /// <param name="month">Period month.</param>
    /// <param name="asOf">
    /// Actual assessment date. Future journal entries are not eligible obligations and aging never
    /// projects beyond this date.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<DelinquentLedgerRow>> GetAsync(
        int year, int month, DateOnly asOf, CancellationToken ct);
}
