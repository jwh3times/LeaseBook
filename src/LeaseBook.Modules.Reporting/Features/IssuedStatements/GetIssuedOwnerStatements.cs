using FluentValidation;
using LeaseBook.Modules.Reporting.Delivery;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Reporting.Features.IssuedStatements;

/// <summary>
/// Every statement already issued to one owner — the immutable <see cref="StatementArtifact"/> rows,
/// newest period first, under ambient org RLS. Each row is a distinct issued document (ADR-040/045): a
/// re-render of a period is listed alongside the earlier one rather than labelled as superseding it,
/// because no amendment semantics are defined.
/// <para>
/// Carries no artifact key, delivery attempt or recipient: nothing here is needed to list documents,
/// and the key is resolved to bytes only by <see cref="GetIssuedOwnerStatementDocument"/>.
/// </para>
/// </summary>
public sealed record GetIssuedOwnerStatements(Guid OwnerId) : IQuery<IReadOnlyList<IssuedOwnerStatement>>;

public sealed class GetIssuedOwnerStatementsValidator : AbstractValidator<GetIssuedOwnerStatements>
{
    public GetIssuedOwnerStatementsValidator() => RuleFor(q => q.OwnerId).NotEmpty();
}

/// <param name="Basis">Null only on artifacts issued before ADR-040 recorded it.</param>
/// <param name="PropertyId">The property scope, or null for a whole-owner statement.</param>
/// <param name="EndingBalance">Null on artifacts issued before ADR-045 recorded it.</param>
/// <param name="IssuedAt">When the artifact was issued (UTC).</param>
public sealed record IssuedOwnerStatement(
    Guid Id, int PeriodYear, int PeriodMonth, string? Basis, Guid? PropertyId, decimal? EndingBalance, DateTime IssuedAt);

internal sealed class GetIssuedOwnerStatementsHandler(DbContext db)
    : IQueryHandler<GetIssuedOwnerStatements, IReadOnlyList<IssuedOwnerStatement>>
{
    public async Task<IReadOnlyList<IssuedOwnerStatement>> Handle(GetIssuedOwnerStatements query, CancellationToken ct) =>
        await db.Set<StatementArtifact>().AsNoTracking()
            .Where(a => a.OwnerId == query.OwnerId)
            .OrderByDescending(a => a.PeriodYear).ThenByDescending(a => a.PeriodMonth)
            .ThenByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .Select(a => new IssuedOwnerStatement(
                a.Id, a.PeriodYear, a.PeriodMonth, a.Basis, a.PropertyId, a.EndingBalance, a.CreatedAt))
            .ToListAsync(ct);
}
