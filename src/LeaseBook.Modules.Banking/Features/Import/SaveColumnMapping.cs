using System.Text.Json;
using FluentValidation;
using LeaseBook.Modules.Banking.Domain;
using LeaseBook.Modules.Banking.Import;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Banking.Features.Import;

/// <summary>
/// Saves (or updates) a named per-bank CSV column mapping (P66) so the operator picks it next time instead
/// of re-mapping. Keyed by (account, name) — re-saving a name overwrites its layout.
/// </summary>
public sealed record SaveColumnMapping(Guid BankAccountId, string Name, ColumnMap ColumnMap)
    : ICommand<SaveColumnMappingResult>;

public sealed record SaveColumnMappingResult(Guid Id);

public sealed class SaveColumnMappingValidator : AbstractValidator<SaveColumnMapping>
{
    public SaveColumnMappingValidator()
    {
        RuleFor(x => x.BankAccountId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.ColumnMap).NotNull();
        When(x => x.ColumnMap is not null, () =>
        {
            RuleFor(x => x.ColumnMap.Date).NotEmpty();
            RuleFor(x => x.ColumnMap.Description).NotEmpty();
        });
    }
}

internal sealed class SaveColumnMappingHandler(DbContext db) : ICommandHandler<SaveColumnMapping, SaveColumnMappingResult>
{
    public async Task<SaveColumnMappingResult> Handle(SaveColumnMapping command, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(command.ColumnMap, ColumnMapJson.Options);

        var existing = await db.Set<BankCsvMapping>()
            .FirstOrDefaultAsync(m => m.BankAccountId == command.BankAccountId && m.Name == command.Name, ct);

        if (existing is null)
        {
            existing = new BankCsvMapping
            {
                Id = UuidV7.NewId(),
                BankAccountId = command.BankAccountId,
                Name = command.Name,
                ColumnMapJson = json,
            };
            db.Set<BankCsvMapping>().Add(existing);
        }
        else
        {
            existing.ColumnMapJson = json;
        }

        await db.SaveChangesAsync(ct);
        return new SaveColumnMappingResult(existing.Id);
    }
}

/// <summary>Shared JSON options for the <see cref="ColumnMap"/> jsonb round-trip (camelCase, the wire shape).</summary>
internal static class ColumnMapJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
