using FluentValidation;
using LeaseBook.Modules.Banking.Import;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Csv;
using LeaseBook.SharedKernel.Tenancy;

namespace LeaseBook.Modules.Banking.Features.Import;

/// <summary>
/// Reads the column names of a bank-statement CSV for the import wizard to offer (ADR-015 §3, #651), through the
/// parser the import itself uses, so any column offered maps. The CSV arrives as text and nothing is stored.
/// </summary>
public sealed record ReadStatementHeaders(string CsvContent) : IQuery<StatementHeaders>;

/// <summary>No names means the file has no named column: the wizard offers none and says why.</summary>
public sealed record StatementHeaders(IReadOnlyList<string> Headers);

public sealed class ReadStatementHeadersValidator : AbstractValidator<ReadStatementHeaders>
{
    public ReadStatementHeadersValidator() =>
        // Empty is allowed, unlike on the import: an empty file is an answer here, not a mistake.
        RuleFor(x => x.CsvContent).NotNull()
            .MaximumLength(CsvImportLimits.MaxCharacters).WithMessage(CsvImportLimits.TooLargeMessage);
}

internal sealed class ReadStatementHeadersHandler(IOrgContext tenant, IStatementParser parser)
    : IQueryHandler<ReadStatementHeaders, StatementHeaders>
{
    public Task<StatementHeaders> Handle(ReadStatementHeaders query, CancellationToken ct)
    {
        _ = tenant.OrgId ?? throw new InvalidOperationException("ReadStatementHeaders requires an ambient organization context.");

        return Task.FromResult(new StatementHeaders(parser.ReadHeaders(query.CsvContent)));
    }
}
