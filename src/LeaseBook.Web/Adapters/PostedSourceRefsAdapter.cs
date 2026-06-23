using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / ADR-019) for the Operations module's <see cref="IPostedSourceRefs"/>
/// port. Dispatches the Accounting <see cref="GetExistingSourceRefs"/> query via
/// <see cref="ISender"/> — Accounting reads its own <c>journal_entries</c> table; no cross-module
/// table touch from Operations.
/// </summary>
internal sealed class PostedSourceRefsAdapter(ISender sender) : IPostedSourceRefs
{
    public async Task<IReadOnlySet<string>> GetExistingAsync(
        IReadOnlyList<string> candidateKeys, CancellationToken ct)
    {
        var response = await sender.Query(new GetExistingSourceRefs(candidateKeys), ct);
        return response.ExistingKeys;
    }
}
