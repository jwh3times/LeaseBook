using FluentValidation;
using LeaseBook.Modules.Reporting.Delivery;
using LeaseBook.SharedKernel.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Reporting.Features.IssuedStatements;

/// <summary>
/// The stored bytes of one issued statement, only when it belongs to <see cref="OwnerId"/>. Never
/// re-renders: the owner receives exactly the document that was issued.
/// <para>
/// The ownership check is part of the lookup, so an artifact belonging to another owner, another
/// organization (RLS) or nobody is one indistinguishable <see cref="IssuedStatementDocumentStatus.NotFound"/>.
/// <see cref="IssuedStatementDocumentStatus.Unavailable"/> is reported only for the owner's own row
/// when the artifact store holds no bytes for its key.
/// </para>
/// </summary>
public sealed record GetIssuedOwnerStatementDocument(Guid OwnerId, Guid ArtifactId) : IQuery<IssuedStatementDocument>;

public sealed class GetIssuedOwnerStatementDocumentValidator : AbstractValidator<GetIssuedOwnerStatementDocument>
{
    public GetIssuedOwnerStatementDocumentValidator() => RuleFor(q => q.OwnerId).NotEmpty();
}

public enum IssuedStatementDocumentStatus
{
    NotFound,
    Unavailable,
    Available,
}

/// <param name="Content">The stored PDF bytes; set only when <paramref name="Status"/> is Available.</param>
public sealed record IssuedStatementDocument(
    IssuedStatementDocumentStatus Status, int PeriodYear = 0, int PeriodMonth = 0, byte[]? Content = null)
{
    public static readonly IssuedStatementDocument NotFound = new(IssuedStatementDocumentStatus.NotFound);
}

internal sealed class GetIssuedOwnerStatementDocumentHandler(DbContext db, IArtifactStore store)
    : IQueryHandler<GetIssuedOwnerStatementDocument, IssuedStatementDocument>
{
    public async Task<IssuedStatementDocument> Handle(GetIssuedOwnerStatementDocument query, CancellationToken ct)
    {
        var artifact = await db.Set<StatementArtifact>().AsNoTracking()
            .Where(a => a.Id == query.ArtifactId && a.OwnerId == query.OwnerId)
            .Select(a => new { a.ArtifactKey, a.PeriodYear, a.PeriodMonth })
            .SingleOrDefaultAsync(ct);
        if (artifact is null)
        {
            return IssuedStatementDocument.NotFound;
        }

        var bytes = await store.GetAsync(artifact.ArtifactKey, ct);
        return bytes is null
            ? new(IssuedStatementDocumentStatus.Unavailable, artifact.PeriodYear, artifact.PeriodMonth)
            : new(IssuedStatementDocumentStatus.Available, artifact.PeriodYear, artifact.PeriodMonth, bytes);
    }
}
