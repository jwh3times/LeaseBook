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

public sealed record MatchPreviewRow(
    Guid StatementLineId, DateOnly Date, string Description, decimal Amount,
    string Kind, Guid? JournalLineId,
    decimal? CandidateAmount, DateOnly? CandidateDate, string? CandidateDescription);

public sealed record MatchPreviewSummary(int Matched, int Suggested, int Unmatched);

public sealed class GetMatchPreviewValidator : AbstractValidator<GetMatchPreview>
{
    public GetMatchPreviewValidator() => RuleFor(q => q.ImportId).NotEmpty();
}

internal sealed class GetMatchPreviewHandler(DbContext db, IBankRegister register)
    : IQueryHandler<GetMatchPreview, MatchPreviewResponse?>
{
    // The register read spans the statement dates plus a generous margin, so an exact-amount candidate that
    // falls outside the ±N match window is still read and surfaced as a "suggested" match (P67).
    private const int ReadMarginDays = 45;

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

        var from = lines.Min(l => l.StatementDate).AddDays(-ReadMarginDays);
        var to = lines.Max(l => l.StatementDate).AddDays(ReadMarginDays);
        var candidates = await register.GetUnclearedAsync(import.BankAccountId, from, to, ct);

        var inputs = lines.Select(l => new MatchInput(l.Id, l.StatementDate, l.Amount.Amount)).ToList();
        var results = AutoMatcher.Match(inputs, candidates);

        var candidateById = candidates.ToDictionary(c => c.JournalLineId);
        var lineById = lines.ToDictionary(l => l.Id);

        var rows = results.Select(r =>
        {
            var line = lineById[r.StatementLineId];
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
