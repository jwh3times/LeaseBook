using FluentValidation;
using FluentValidation.Results;
using LeaseBook.Modules.Banking.Domain;
using LeaseBook.Modules.Banking.Import;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Csv;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Banking.Features.Import;

/// <summary>
/// Imports a bank-statement CSV for one account (ADR-015): parses it against the column map, dedups against
/// every prior line for that account (P67), and stores the new lines under one <see cref="StatementImport"/>.
/// A colliding line is never stored twice: one with a recorded decision is skipped and counted, and one with
/// none joins this import, so the preview offers it again (#671). The CSV arrives as text (the SPA reads the
/// file client-side); parse errors are reported per row, never fatal. The one file-level refusal is a column
/// map naming a column the file lacks (a 400, nothing stored; #645).
/// </summary>
public sealed record ImportStatement(
    Guid BankAccountId, string Filename, string CsvContent, ColumnMap ColumnMap) : ICommand<ImportResult>;

/// <param name="Imported">Lines this file added.</param>
/// <param name="BroughtBack">Earlier lines still waiting for a decision that this file brought back (#671).</param>
/// <param name="SkippedDuplicates">Rows that added nothing and brought nothing back.</param>
public sealed record ImportResult(
    Guid ImportId, int Imported, int BroughtBack, int SkippedDuplicates, IReadOnlyList<RowError> Errors);

public sealed class ImportStatementValidator : AbstractValidator<ImportStatement>
{
    public ImportStatementValidator()
    {
        RuleFor(x => x.BankAccountId).NotEmpty();
        RuleFor(x => x.Filename).NotEmpty();
        RuleFor(x => x.CsvContent).NotEmpty()
            .MaximumLength(CsvImportLimits.MaxCharacters).WithMessage(CsvImportLimits.TooLargeMessage);
        RuleFor(x => x.ColumnMap).NotNull();
        When(x => x.ColumnMap is not null, () =>
        {
            RuleFor(x => x.ColumnMap.Date).NotEmpty();
            RuleFor(x => x.ColumnMap.Description).NotEmpty();
            RuleFor(x => x.ColumnMap)
                .Must(m => !string.IsNullOrWhiteSpace(m.Amount)
                           || !string.IsNullOrWhiteSpace(m.Debit) || !string.IsNullOrWhiteSpace(m.Credit))
                .WithMessage("The column map needs an amount column, or a debit and/or credit column.");
        });
    }
}

internal sealed class ImportStatementHandler(
    DbContext db, IOrgContext tenant, IActorContext actor, IStatementParser parser)
    : ICommandHandler<ImportStatement, ImportResult>
{
    public async Task<ImportResult> Handle(ImportStatement command, CancellationToken ct)
    {
        _ = tenant.OrgId ?? throw new InvalidOperationException("ImportStatement requires an ambient organization context.");

        var parsed = parser.Parse(command.CsvContent, command.ColumnMap);

        // A map the file does not fit is refused whole, before anything is stored (#645): one failure per
        // absent column, keyed like the validator's own ColumnMap.* failures.
        if (parsed.MissingColumns.Count > 0)
        {
            throw new ValidationException(parsed.MissingColumns.Select(c => new ValidationFailure(
                $"{nameof(ImportStatement.ColumnMap)}.{c.Field}",
                $"The file has no column named '{c.Name}'. Check the column mapping against the file's header row.")));
        }

        await StatementImportLock.AcquireAsync(db, command.BankAccountId, ct);

        // Dedup spans every prior import for this account (RLS scopes the read to the org).
        var existingHashes = await db.Set<StatementLine>()
            .Where(l => l.BankAccountId == command.BankAccountId)
            .Select(l => l.DedupHash)
            .ToListAsync(ct);
        var seen = new HashSet<string>(existingHashes, StringComparer.Ordinal);

        // A line with no recorded decision can only reach a confirm through an import's preview, so one this
        // file repeats joins this import rather than being skipped (#671).
        var undecided = await db.Set<StatementLine>()
            .Where(l => l.BankAccountId == command.BankAccountId
                        && !db.Set<StatementMatch>().Any(m => m.StatementLineId == l.Id))
            .ToDictionaryAsync(l => l.DedupHash, StringComparer.Ordinal, ct);

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
        var broughtBack = 0;
        var skipped = 0;
        foreach (var row in parsed.Rows)
        {
            var hash = DedupHash.Compute(row.Date, row.Amount, row.Description);
            if (!seen.Add(hash))
            {
                // Removed once found, so a row repeated in this file returns its line only once.
                if (undecided.Remove(hash, out var line))
                {
                    line.ImportId = import.Id;
                    broughtBack++;
                }
                else
                {
                    skipped++; // collides with a decided line, or an identical earlier row in this same file
                }

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

        import.RowCount = newLines.Count + broughtBack;
        db.Set<StatementImport>().Add(import);
        db.Set<StatementLine>().AddRange(newLines);
        await db.SaveChangesAsync(ct);

        return new ImportResult(import.Id, newLines.Count, broughtBack, skipped, parsed.Errors);
    }
}
