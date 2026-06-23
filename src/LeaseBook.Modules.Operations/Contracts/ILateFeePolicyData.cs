using LeaseBook.Modules.Operations.Runs;

namespace LeaseBook.Modules.Operations.Contracts;

// ── Data DTO ─────────────────────────────────────────────────────────────────

/// <summary>
/// The effective late-fee policy for one lease, returned by <see cref="ILateFeePolicyData"/>.
/// The host adapter resolves (per-lease override ?? org default) per field in Directory before
/// mapping to the Operations-owned <see cref="LateFeePolicy"/> type (ADR-007).
/// </summary>
public sealed record LateFeePolicyRow(Guid LeaseId, LateFeePolicy Effective);

// ── Port ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Read-direction cross-module port (ADR-007 / WP-3). Operations declares the interface;
/// the host adapter (<c>LateFeePolicyDataAdapter</c>) implements it by dispatching the Directory
/// <c>GetLateFeePolicies</c> query via <see cref="LeaseBook.SharedKernel.Cqrs.ISender"/> and
/// mapping the result to Operations-owned types.
/// <para>
/// Returns the effective late-fee policy per lease: each field is the per-lease override if set,
/// or the org-default otherwise (resolved in Directory, never here).
/// </para>
/// </summary>
public interface ILateFeePolicyData
{
    /// <summary>
    /// Returns a map of <c>leaseId → <see cref="LateFeePolicy"/></c> for the given leases.
    /// Only leases present in <paramref name="leaseIds"/> are returned; missing ids were not found
    /// in the active schedule and will be excluded by the strategy.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, LateFeePolicy>> GetAsync(
        IReadOnlyList<Guid> leaseIds, CancellationToken ct);
}
