using FluentValidation;
using LeaseBook.Modules.Banking.Domain;
using LeaseBook.Modules.Banking.Import;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Banking.Features.Import;

/// <summary>
/// Imports a bank-statement CSV for one account (ADR-015): parses it against the column map, dedups against
/// every prior line for that account (P67), and stores the new lines under one <see cref="StatementImport"/>.
/// Idempotent on re-import — colliding lines are skipped and counted, never stored twice. The CSV arrives as
/// text (the SPA reads the file client-side); parse errors are reported per row, never fatal.
/// </summary>
public sealed record ImportStatement(
    Guid BankAccountId, string Filename, string CsvContent, ColumnMap ColumnMap) : ICommand<ImportResult>;

public sealed record ImportResult(
    Guid ImportId, int Imported, int SkippedDuplicates, IReadOnlyList<RowError> Errors);

public sealed class ImportStatementValidator : AbstractValidator<ImportStatement>
{
    public ImportStatementValidator()
    {
        RuleFor(x => x.BankAccountId).NotEmpty();
        RuleFor(x => x.Filename).NotEmpty();
        RuleFor(x => x.CsvContent).NotEmpty();
        RuleFor(x => x.ColumnMap).NotNull();
        When(x => x.ColumnMap is not null, () =>
        {
            RuleFor(x => x.ColumnMap.Date).NotEmpty();
            RuleFor(x => x.ColumnMap.Description).NotEmpty();
            RuleFor(x => x.ColumnMap)
                .Must(m => !string.IsNullOrWhiteSpace(m.Amount)
                           || !string.IsNullOrWhiteSpace(m.Debit) || !string.IsNullOrWhiteSpace(m.Credit))
                .WithMessage("The column map needs an amount column, or a debit and credit column.");
        });
    }
}

internal sealed class ImportStatementHandler(
    DbContext db, ITenantContext tenant, IActorContext actor, IStatementParser parser)
    : ICommandHandler<ImportStatement, ImportResult>
{
    public async Task<ImportResult> Handle(ImportStatement command, CancellationToken ct)
    {
        _ = tenant.OrgId ?? throw new InvalidOperationException("ImportStatement requires an ambient org context.");

        var parsed = parser.Parse(command.CsvContent, command.ColumnMap);

        // Dedup spans every prior import for this account (RLS scopes the read to the org).
        var existingHashes = await db.Set<StatementLine>()
            .Where(l => l.BankAccountId == command.BankAccountId)
            .Select(l => l.DedupHash)
            .ToListAsync(ct);
        var seen = new HashSet<string>(existingHashes, StringComparer.Ordinal);

        var import = new StatementImport
        {
            Id = UuidV7.NewId(),
            BankAccountId = command.BankAccountId,
            Filename = command.Filename,
            ImportedAt = DateTime.UtcNow,
            ImportedBy = actor.UserId,
            Status = "completed",
        };

        var newLines = new List<StatementLine>();
        var skipped = 0;
        foreach (var row in parsed.Rows)
        {
            var hash = DedupHash.Compute(row.Date, row.Amount, row.Description);
            if (!seen.Add(hash))
            {
                skipped++; // collides with a prior import, or an identical earlier row in this same file
                continue;
            }

            newLines.Add(new StatementLine
            {
                Id = UuidV7.NewId(),
                BankAccountId = command.BankAccountId,
                ImportId = import.Id,
                StatementDate = row.Date,
                Description = row.Description,
                Amount = new Money(row.Amount),
                DedupHash = hash,
            });
        }

        import.RowCount = newLines.Count;
        db.Set<StatementImport>().Add(import);
        db.Set<StatementLine>().AddRange(newLines);
        await db.SaveChangesAsync(ct);

        return new ImportResult(import.Id, newLines.Count, skipped, parsed.Errors);
    }
}
