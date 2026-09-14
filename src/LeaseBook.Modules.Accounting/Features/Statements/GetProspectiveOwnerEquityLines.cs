using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Modules.Accounting.Features.Statements;

/// <summary>
/// The owner-equity footprint that supported bulk-run events would post, without saving. The result
/// contains exactly one line per input event in the same order, so a caller can retain its target
/// correlation without putting an Operations concept into Accounting.
/// </summary>
public sealed record GetProspectiveOwnerEquityLines(IReadOnlyList<AccountingEvent> Events)
    : IQuery<IReadOnlyList<ProspectiveOwnerEquityLine>>;

public sealed class GetProspectiveOwnerEquityLinesValidator : AbstractValidator<GetProspectiveOwnerEquityLines>
{
    public GetProspectiveOwnerEquityLinesValidator()
    {
        RuleFor(q => q.Events)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .Must(events => events.All(e => RunEventEntryBuilder.TryBuild(e, out _)))
            .WithMessage("Only bulk-run accounting events can be projected.");
    }
}

/// <param name="Amount">Net owner-equity movement (credit − debit).</param>
public sealed record ProspectiveOwnerEquityLine(
    Guid OwnerId, Guid? PropertyId, string Basis, DateOnly EntryDate, decimal Amount);

internal sealed class GetProspectiveOwnerEquityLinesHandler
    : IQueryHandler<GetProspectiveOwnerEquityLines, IReadOnlyList<ProspectiveOwnerEquityLine>>
{
    public Task<IReadOnlyList<ProspectiveOwnerEquityLine>> Handle(
        GetProspectiveOwnerEquityLines q,
        CancellationToken ct)
    {
        var lines = q.Events.Select(Project).ToArray();

        return Task.FromResult<IReadOnlyList<ProspectiveOwnerEquityLine>>(lines);
    }

    private static ProspectiveOwnerEquityLine Project(AccountingEvent businessEvent)
    {
        var entry = RunEventEntryBuilder.Build(businessEvent);
        var line = entry.Lines.Single(line => line.AccountCode == AccountCodes.OwnerEquity);

        return new ProspectiveOwnerEquityLine(
            line.OwnerId ?? throw new InvalidOperationException("A run event's owner-equity line must name an owner."),
            line.PropertyId,
            BasisName(line.Basis),
            entry.EntryDate,
            (line.Credit?.Amount ?? 0m) - (line.Debit?.Amount ?? 0m));
    }

    private static string BasisName(EntryBasis basis) => basis switch
    {
        EntryBasis.Cash => "cash",
        EntryBasis.Accrual => "accrual",
        EntryBasis.Both => "both",
        _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, null),
    };
}
