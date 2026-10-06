using FluentValidation;
using LeaseBook.Modules.Banking.Contracts;
using LeaseBook.Modules.Banking.Domain;
using LeaseBook.Modules.Banking.Import;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Banking.Features.Import;

/// <summary>
/// The match preview for an import (P67): runs the auto-match heuristic over the import's lines against the
/// uncleared register candidates (read through the <see cref="IBankRegister"/> port — ADR-007, no
/// cross-module SQL) and returns each line classified matched/suggested/unmatched, with the candidate it
/// resolved to. A read — it persists nothing; <see cref="ConfirmMatches"/> records the decisions and clears.
/// Returns null when the import does not exist (→ 404).
/// </summary>
public sealed record GetMatchPreview(Guid ImportId) : IQuery<MatchPreviewResponse?>;

public sealed record MatchPreviewResponse(IReadOnlyList<MatchPreviewRow> Rows, MatchPreviewSummary Summary);

/// <param name="GroupRef">
/// Set instead of <paramref name="JournalLineId"/> when the line matched a whole group of register lines:
/// a processor payout (ADR-053). <paramref name="GroupLines"/> lists them; they clear together.
/// </param>
public sealed record MatchPreviewRow(
    Guid StatementLineId, DateOnly Date, string Description, decimal Amount,
    string Kind, Guid? JournalLineId,
    decimal? CandidateAmount, DateOnly? CandidateDate, string? CandidateDescription,
    string? GroupRef = null, IReadOnlyList<MatchPreviewGroupLine>? GroupLines = null);

public sealed record MatchPreviewGroupLine(Guid JournalLineId, DateOnly Date, decimal Amount, string Description);

public sealed record MatchPreviewSummary(int Matched, int Suggested, int Unmatched);

public sealed class GetMatchPreviewValidator : AbstractValidator<GetMatchPreview>
{
    public GetMatchPreviewValidator() => RuleFor(q => q.ImportId).NotEmpty();
}

internal sealed class GetMatchPreviewHandler(DbContext db, IBankRegister register)
    : IQueryHandler<GetMatchPreview, MatchPreviewResponse?>
{
    public async Task<MatchPreviewResponse?> Handle(GetMatchPreview query, CancellationToken ct)
    {
        var import = await db.Set<StatementImport>().FirstOrDefaultAsync(i => i.Id == query.ImportId, ct);
        if (import is null)
        {
            return null;
        }

        var lines = await db.Set<StatementLine>()
            .Where(l => l.ImportId == query.ImportId)
            .OrderBy(l => l.StatementDate).ThenBy(l => l.Id)
            .ToListAsync(ct);

        if (lines.Count == 0)
        {
            return new MatchPreviewResponse([], new MatchPreviewSummary(0, 0, 0));
        }

        var candidates = await MatchCandidates.ReadAsync(register, import.BankAccountId, lines, ct);

        var inputs = lines.Select(l => new MatchInput(l.Id, l.StatementDate, l.Amount.Amount)).ToList();
        var results = AutoMatcher.Match(inputs, candidates);

        var candidateById = candidates.ToDictionary(c => c.JournalLineId);
        var groups = AutoMatcher.Groups(candidates);
        var lineById = lines.ToDictionary(l => l.Id);

        var rows = results.Select(r =>
        {
            var line = lineById[r.StatementLineId];
            if (r.GroupRef is { } groupRef)
            {
                var group = groups[groupRef];
                return new MatchPreviewRow(
                    r.StatementLineId, line.StatementDate, line.Description, line.Amount.Amount,
                    r.Kind.ToDb(), null,
                    group.Sum(c => c.Amount), AutoMatcher.GroupDate(group), $"Payout {groupRef}",
                    groupRef,
                    [.. group.Select(c => new MatchPreviewGroupLine(c.JournalLineId, c.Date, c.Amount, c.Description))]);
            }

            RegisterCandidate? candidate =
                r.JournalLineId is { } jid && candidateById.TryGetValue(jid, out var c) ? c : null;
            return new MatchPreviewRow(
                r.StatementLineId, line.StatementDate, line.Description, line.Amount.Amount,
                r.Kind.ToDb(), r.JournalLineId,
                candidate?.Amount, candidate?.Date, candidate?.Description);
        }).ToList();

        var summary = new MatchPreviewSummary(
            rows.Count(x => x.Kind == MatchKinds.Matched),
            rows.Count(x => x.Kind == MatchKinds.Suggested),
            rows.Count(x => x.Kind == MatchKinds.Unmatched));

        return new MatchPreviewResponse(rows, summary);
    }
}

/// <summary>
/// The register read behind both the preview and the confirmation, so a confirmation judges a group
/// against exactly the candidates the preview offered it from.
/// </summary>
internal static class MatchCandidates
{
    // The read spans the statement dates plus a generous margin, so an exact-amount candidate that falls
    // outside the ±N match window is still read and surfaced as a "suggested" match (P67).
    private const int ReadMarginDays = 45;

    public static Task<IReadOnlyList<RegisterCandidate>> ReadAsync(
        IBankRegister register, Guid bankAccountId, IReadOnlyCollection<StatementLine> lines, CancellationToken ct) =>
        register.GetUnclearedAsync(
            bankAccountId,
            lines.Min(l => l.StatementDate).AddDays(-ReadMarginDays),
            lines.Max(l => l.StatementDate).AddDays(ReadMarginDays),
            ct);
}
