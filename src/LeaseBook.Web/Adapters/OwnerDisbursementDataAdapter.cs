using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / WP-4) for Operations' <see cref="IOwnerDisbursementData"/> port.
/// Dispatches Directory's <see cref="GetOwnerDisbursementData"/> query via <see cref="ISender"/>
/// and maps the response to Operations-owned <see cref="OwnerDisbursementRow"/> DTOs
/// (no cross-module type bleed into Modules.Operations).
/// </summary>
internal sealed class OwnerDisbursementDataAdapter(ISender sender) : IOwnerDisbursementData
{
    public async Task<IReadOnlyList<OwnerDisbursementRow>> GetAsync(CancellationToken ct)
    {
        var rows = await sender.Query(new GetOwnerDisbursementData(), ct);
        return rows
            .Select(r => new OwnerDisbursementRow(r.OwnerId, r.Name, r.ReserveAmount, r.DefaultMgmtFeeBps))
            .ToList();
    }
}
