namespace LeaseBook.Modules.Operations.Contracts;

// ── Data DTO ─────────────────────────────────────────────────────────────────

/// <summary>
/// One owner row for the disbursement run — name, reserve floor, and default mgmt-fee bps.
/// Mirrors <c>DirOwnerDisbursementRow</c> in Directory without crossing the module boundary
/// (ADR-007: Operations references SharedKernel only; the host adapter translates).
/// </summary>
public sealed record OwnerDisbursementRow(
    Guid OwnerId,
    string Name,
    decimal ReserveAmount,
    int? DefaultMgmtFeeBps);

// ── Port ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Read-direction cross-module port (ADR-007 / WP-4). Operations declares the interface;
/// the host adapter (<c>OwnerDisbursementDataAdapter</c>) implements it by dispatching
/// Directory's <c>GetOwnerDisbursementData</c> query via <see cref="LeaseBook.SharedKernel.Cqrs.ISender"/>.
/// <para>
/// Returns all non-system owners — the universe of candidates for the disbursement run.
/// </para>
/// </summary>
public interface IOwnerDisbursementData
{
    Task<IReadOnlyList<OwnerDisbursementRow>> GetAsync(CancellationToken ct);
}
