namespace LeaseBook.Modules.Operations.Contracts;

/// <summary>
/// Read-direction cross-module port (ADR-007). Operations declares this interface; the host adapter
/// (<c>PeriodChargeGuardAdapter</c>) implements it by dispatching an Accounting
/// <c>GetTenantsChargedInPeriod</c> query via <see cref="LeaseBook.SharedKernel.Cqrs.ISender"/>.
/// <para>
/// Used by <see cref="RentRunStrategy"/> and <see cref="LateFeeRunStrategy"/> to detect tenants
/// that already have a charge of the given event type in the period, regardless of how that charge
/// was posted (manual M3 composer, seed, CSV import, or a prior bulk run). This is the structural
/// cross-source double-charge guard: the <c>IPostedSourceRefs</c> port only detects idempotent
/// re-runs of the SAME bulk key; this port detects ANY charge from ANY source.
/// </para>
/// </summary>
public interface IPeriodChargeGuard
{
    /// <summary>
    /// Returns the subset of <paramref name="tenantIds"/> for which a journal entry with
    /// <paramref name="eventType"/> (and <paramref name="eventSubtype"/> when non-null) already
    /// exists within the calendar month <paramref name="year"/>/<paramref name="month"/>.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetChargedTenantsAsync(
        string eventType,
        string? eventSubtype,
        int year,
        int month,
        IReadOnlyList<Guid> tenantIds,
        CancellationToken ct);
}
