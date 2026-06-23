namespace LeaseBook.Modules.Operations.Contracts;

// ── Data DTO ─────────────────────────────────────────────────────────────────

/// <summary>
/// One active lease row returned by <see cref="ILeaseScheduleData.GetActiveAsync"/>.
/// Mirrors <c>LeaseBook.Modules.Directory.Features.Reporting.LeaseScheduleRow</c> without
/// crossing the module boundary (ADR-007 — Operations references SharedKernel only; the host
/// adapter translates).
/// </summary>
public sealed record LeaseScheduleRow(
    Guid LeaseId,
    Guid TenantId,
    Guid PropertyId,
    Guid OwnerId,
    Guid? UnitId,
    string TenantName,
    string UnitLabel,
    decimal Rent,
    DateOnly? StartDate,
    DateOnly? EndDate);

// ── Port ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Read-direction cross-module port (ADR-007 / ADR-019). Operations declares the interface;
/// the host adapter (<c>LeaseScheduleDataAdapter</c>) implements it by dispatching the Directory
/// <c>GetActiveLeaseSchedule</c> query via <see cref="LeaseBook.SharedKernel.Cqrs.ISender"/>.
/// <para>
/// Returns all active leases whose term overlaps the requested period — the universe of
/// candidates for the <see cref="RentRunStrategy"/>.
/// </para>
/// </summary>
public interface ILeaseScheduleData
{
    /// <summary>
    /// Returns the active-lease schedule for the given calendar month. Filters are applied
    /// inside the Directory query (status, term overlap, NotSystem on every join).
    /// </summary>
    Task<IReadOnlyList<LeaseScheduleRow>> GetActiveAsync(int year, int month, CancellationToken ct);
}
