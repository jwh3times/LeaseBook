using LeaseBook.Modules.Directory.Features.Reporting;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Operations.Runs;
using LeaseBook.SharedKernel.Cqrs;
using DirKind = LeaseBook.Modules.Directory.Domain.LateFeeKind;
using OpsKind = LeaseBook.Modules.Operations.Runs.LateFeeKind;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / WP-3) for the Operations module's <see cref="ILateFeePolicyData"/>
/// port. Dispatches the Directory <see cref="GetLateFeePolicies"/> query via
/// <see cref="ISender"/> and maps the response to Operations-owned <see cref="LateFeePolicy"/>
/// types (no cross-module type bleed into Modules.Operations).
/// </summary>
internal sealed class LateFeePolicyDataAdapter(ISender sender) : ILateFeePolicyData
{
    public async Task<IReadOnlyDictionary<Guid, LateFeePolicy>> GetAsync(
        IReadOnlyList<Guid> leaseIds, CancellationToken ct)
    {
        var response = await sender.Query(new GetLateFeePolicies(leaseIds), ct);

        return response.Rows
            .ToDictionary(
                r => r.LeaseId,
                r => new LateFeePolicy(
                    r.RentDueDay,
                    r.GraceDays,
                    MapKind(r.Kind),
                    r.FlatAmount,
                    r.RateBps));
    }

    // Translates Directory's LateFeeKind to Operations' LateFeeKind (ADR-007: each module owns its enum copy;
    // the host adapter is the boundary translator).
    private static OpsKind MapKind(DirKind kind) => kind switch
    {
        DirKind.Flat => OpsKind.Flat,
        DirKind.Percent => OpsKind.Percent,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Directory LateFeeKind."),
    };
}
