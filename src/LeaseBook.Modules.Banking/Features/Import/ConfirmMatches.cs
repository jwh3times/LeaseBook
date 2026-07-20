using FluentValidation;
using LeaseBook.Modules.Banking.Contracts;
using LeaseBook.Modules.Banking.Domain;
using LeaseBook.Modules.Banking.Import;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Banking.Features.Import;

/// <summary>
/// Commits the user's reviewed match decisions (P67): records each as a <see cref="StatementMatch"/> (audit)
/// and clears the matched/suggested/created register lines through the <see cref="IBankClearing"/> port
/// (ADR-007 — Accounting owns the clearance write). Unmatched lines clear nothing and are returned as the
/// create-transaction prompts the UI routes to the bank-adjustment endpoint.
/// </summary>
public sealed record ConfirmMatches(Guid ImportId, IReadOnlyList<MatchDecision> Decisions)
    : ICommand<ConfirmMatchesResult>;

public sealed record MatchDecision(Guid StatementLineId, Guid? JournalLineId, string Kind);

public sealed record ConfirmMatchesResult(int Cleared, int Recorded, IReadOnlyList<Guid> UnmatchedLineIds);

public sealed class ConfirmMatchesValidator : AbstractValidator<ConfirmMatches>
{
    public ConfirmMatchesValidator()
    {
        RuleFor(x => x.ImportId).NotEmpty();
        RuleFor(x => x.Decisions).NotEmpty();
        RuleForEach(x => x.Decisions).ChildRules(d =>
        {
            d.RuleFor(x => x.StatementLineId).NotEmpty();
            d.RuleFor(x => x.Kind).Must(MatchKinds.All.Contains)
                .WithMessage($"Kind must be one of: {string.Join(", ", MatchKinds.All)}.");
            d.RuleFor(x => x.JournalLineId).NotNull()
                .When(x => MatchKinds.ClearsRegisterLine(x.Kind))
                .WithMessage("A matched, suggested, or created decision must carry a journal line id.");
        });
    }
}

internal sealed class ConfirmMatchesHandler(DbContext db, IActorContext actor, IBankClearing clearing)
    : ICommandHandler<ConfirmMatches, ConfirmMatchesResult>
{
    public async Task<ConfirmMatchesResult> Handle(ConfirmMatches command, CancellationToken ct)
    {
        var toClear = command.Decisions
            .Where(d => MatchKinds.ClearsRegisterLine(d.Kind) && d.JournalLineId is not null)
            .Select(d => d.JournalLineId!.Value)
            .Distinct()
            .ToList();

        foreach (var decision in command.Decisions)
        {
            db.Set<StatementMatch>().Add(new StatementMatch
            {
                Id = UuidV7.NewId(),
                StatementLineId = decision.StatementLineId,
                JournalLineId = decision.JournalLineId,
                Kind = decision.Kind,
                DecidedAt = DateTime.UtcNow,
                DecidedBy = actor.UserId,
            });
        }

        await db.SaveChangesAsync(ct);

        if (toClear.Count > 0)
        {
            await clearing.ApplyClearancesAsync(toClear, ct);
        }

        var unmatched = command.Decisions
            .Where(d => d.Kind == MatchKinds.Unmatched)
            .Select(d => d.StatementLineId)
            .ToList();

        return new ConfirmMatchesResult(toClear.Count, command.Decisions.Count, unmatched);
    }
}
