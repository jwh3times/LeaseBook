using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Reporting.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007/016) for Reporting's <see cref="IStatementNames"/> port. Delegates to
/// Directory's <see cref="GetOwnerLookup"/> and <see cref="GetPropertyLookup"/> queries via
/// <see cref="ISender"/> and returns batch maps (id → name/address) so the statement assembler
/// does not touch Directory's tables directly.
/// </summary>
internal sealed class StatementNamesAdapter(ISender sender) : IStatementNames
{
    public async Task<IReadOnlyDictionary<Guid, string>> GetOwnerNamesAsync(CancellationToken ct)
    {
        var rows = await sender.Query(new GetOwnerLookup(), ct);
        return rows.ToDictionary(r => r.Id, r => r.Name);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetPropertyAddressesAsync(CancellationToken ct)
    {
        var rows = await sender.Query(new GetPropertyLookup(), ct);
        return rows.ToDictionary(r => r.Id, r => r.Address);
    }
}
