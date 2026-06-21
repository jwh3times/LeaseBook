using System.Text.Json;
using FluentValidation;
using LeaseBook.Modules.Banking.Domain;
using LeaseBook.Modules.Banking.Import;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Banking.Features.Import;

/// <summary>Lists the saved CSV column mappings for one bank account (P66) so the import wizard can offer them.</summary>
public sealed record GetColumnMappings(Guid BankAccountId) : IQuery<ColumnMappingsResponse>;

public sealed record ColumnMappingsResponse(IReadOnlyList<ColumnMappingView> Mappings);

public sealed record ColumnMappingView(Guid Id, string Name, ColumnMap ColumnMap);

public sealed class GetColumnMappingsValidator : AbstractValidator<GetColumnMappings>
{
    public GetColumnMappingsValidator() => RuleFor(q => q.BankAccountId).NotEmpty();
}

internal sealed class GetColumnMappingsHandler(DbContext db) : IQueryHandler<GetColumnMappings, ColumnMappingsResponse>
{
    public async Task<ColumnMappingsResponse> Handle(GetColumnMappings query, CancellationToken ct)
    {
        var rows = await db.Set<BankCsvMapping>()
            .Where(m => m.BankAccountId == query.BankAccountId)
            .OrderBy(m => m.Name)
            .Select(m => new { m.Id, m.Name, m.ColumnMapJson })
            .ToListAsync(ct);

        var mappings = rows
            .Select(r => new ColumnMappingView(
                r.Id, r.Name, JsonSerializer.Deserialize<ColumnMap>(r.ColumnMapJson, ColumnMapJson.Options)!))
            .ToList();

        return new ColumnMappingsResponse(mappings);
    }
}
