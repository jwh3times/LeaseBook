using FluentValidation;
using FluentValidation.Results;
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
/// <para>
/// A decision names one register line or one whole group (<see cref="MatchDecision.GroupRef"/>), a
/// processor payout (ADR-053). A group is judged again here, against the register as it is now: its
/// lines must still sum to the statement line's amount, or nothing is recorded and nothing clears. A
/// register line that belongs to a group is refused as a match of its own.
/// </para>
/// </summary>
public sealed record ConfirmMatches(Guid ImportId, IReadOnlyList<MatchDecision> Decisions)
    : ICommand<ConfirmMatchesResult>;

public sealed record MatchDecision(Guid StatementLineId, Guid? JournalLineId, string Kind, string? GroupRef = null);

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
            d.RuleFor(x => x.GroupRef).MaximumLength(100);
            d.RuleFor(x => x).Must(x => (x.JournalLineId is null) != (x.GroupRef is null))
                .When(x => MatchKinds.ClearsRegisterLine(x.Kind))
                .WithMessage("A matched, suggested, or created decision must carry a journal line id or a group, not both.");
            d.RuleFor(x => x.GroupRef).Null()
                .When(x => x.Kind is MatchKinds.Created or MatchKinds.Unmatched)
                .WithMessage("Only a matched or suggested decision can name a group.");
        });
    }
}

internal sealed class ConfirmMatchesHandler(
    DbContext db, IActorContext actor, IBankClearing clearing, IBankRegister register)
    : ICommandHandler<ConfirmMatches, ConfirmMatchesResult>
{
    public async Task<ConfirmMatchesResult> Handle(ConfirmMatches command, CancellationToken ct)
    {
        var groupLines = await GroupLinesAsync(command, ct);

        var toClear = command.Decisions
            .Where(d => MatchKinds.ClearsRegisterLine(d.Kind))
            .SelectMany(d => d.GroupRef is { } groupRef ? groupLines[groupRef] : [d.JournalLineId!.Value])
            .Distinct()
            .ToList();

        // One audit row per register line: a group's rows share the statement line they answer.
        foreach (var decision in command.Decisions)
        {
            IEnumerable<Guid?> matched = decision.GroupRef is { } groupRef
                ? groupLines[groupRef].Select(id => (Guid?)id)
                : [decision.JournalLineId];
            foreach (var journalLineId in matched)
            {
                db.Set<StatementMatch>().Add(new StatementMatch
                {
                    Id = UuidV7.NewId(),
                    StatementLineId = decision.StatementLineId,
                    JournalLineId = journalLineId,
                    Kind = decision.Kind,
                    DecidedAt = DateTime.UtcNow,
                    DecidedBy = actor.UserId,
                });
            }
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

    // Returns each named group's lines, after the server's own checks: the preview is advice, and the
    // register may have moved since.
    private async Task<Dictionary<string, List<Guid>>> GroupLinesAsync(ConfirmMatches command, CancellationToken ct)
    {
        var result = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        var clearing = command.Decisions.Where(d => MatchKinds.ClearsRegisterLine(d.Kind)).ToList();

        // A line of a group never clears as a line of its own. Asked of the journal, not of the candidate
        // read: that read is windowed and uncleared-only, and clearing one line clears its whole group.
        var single = clearing.Where(d => d.GroupRef is null).Select(d => d.JournalLineId!.Value).Distinct().ToList();
        if (single.Count > 0 && (await register.GetGroupRefsAsync(single, ct)).Count > 0)
        {
            throw Refused("A bank line that belongs to a payout clears only with the whole payout.");
        }

        var byGroup = clearing.Where(d => d.GroupRef is not null).ToList();
        if (byGroup.Count == 0)
        {
            return result;
        }

        var import = await db.Set<StatementImport>().FirstOrDefaultAsync(i => i.Id == command.ImportId, ct);
        var lines = await db.Set<StatementLine>().Where(l => l.ImportId == command.ImportId).ToListAsync(ct);
        if (import is null || lines.Count == 0)
        {
            throw Refused("This import has no statement lines to match a payout against.");
        }

        var groups = AutoMatcher.Groups(await MatchCandidates.ReadAsync(register, import.BankAccountId, lines, ct));
        var lineById = lines.ToDictionary(l => l.Id);

        foreach (var decision in byGroup)
        {
            var groupRef = decision.GroupRef!;
            // A group that nets to nothing answers no statement line, as in the matcher.
            if (!lineById.TryGetValue(decision.StatementLineId, out var line)
                || !groups.TryGetValue(groupRef, out var group)
                || group.Sum(c => c.Amount) != line.Amount.Amount
                || line.Amount.Amount == 0m
                || !result.TryAdd(groupRef, [.. group.Select(c => c.JournalLineId)]))
            {
                throw Refused($"Payout {groupRef} no longer matches its statement line. Preview the matches again.");
            }
        }

        return result;
    }

    // Raised as a named failure so the message reaches the response body, not only the status code.
    private static ValidationException Refused(string message) =>
        new([new ValidationFailure(nameof(ConfirmMatches.Decisions), message)]);
}
