using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Statements;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Operations.Runs;
using LeaseBook.Modules.Reporting.Contracts;
using LeaseBook.Modules.Reporting.Delivery;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.Web.Adapters;
using LeaseBook.Web.Operations;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.Web.Reporting;

/// <summary>One prospective run target and the issued statement its posting would affect.</summary>
public sealed record RunPreviewIssuedCoverageRow(
    Guid TargetId,
    Guid OwnerId,
    string OwnerName,
    string Basis,
    Guid? PropertyId,
    string? PropertyAddress,
    int IssuedYear,
    int IssuedMonth);

/// <summary>SPA shape for a run preview's issued-statement coverage read.</summary>
public sealed record RunPreviewIssuedCoverageResponse(IReadOnlyList<RunPreviewIssuedCoverageRow> Rows);

/// <summary>Reads prospective issued-statement coverage for one bulk-run preview period.</summary>
public sealed record GetRunPreviewIssuedCoverage(string Type, int? Year, int? Month)
    : IQuery<RunPreviewIssuedCoverageResponse>;

public static class RunPreviewIssuedCoverageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the host-owned cross-module slice before the shared CQRS registration decorates
    /// every handler. The whole host assembly is intentionally not scanned because Auth owns and
    /// registers its endpoint-filter validators separately.
    /// </summary>
    public static IServiceCollection AddRunPreviewIssuedCoverage(this IServiceCollection services) =>
        services
            .AddScoped<IQueryHandler<GetRunPreviewIssuedCoverage, RunPreviewIssuedCoverageResponse>,
                GetRunPreviewIssuedCoverageHandler>()
            .AddScoped<IValidator<GetRunPreviewIssuedCoverage>, GetRunPreviewIssuedCoverageValidator>();
}

public sealed class GetRunPreviewIssuedCoverageValidator : AbstractValidator<GetRunPreviewIssuedCoverage>
{
    public GetRunPreviewIssuedCoverageValidator()
    {
        RuleFor(q => q.Type)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(type => RunTypeRoute.TryParse(type, out _))
            .WithMessage("That is not a run type this screen supports.");
        RuleFor(q => q.Year)
            .InclusiveBetween(2000, 2100)
            .When(q => q.Year.HasValue);
        RuleFor(q => q.Month)
            .InclusiveBetween(1, 12)
            .When(q => q.Month.HasValue);
    }
}

/// <summary>
/// Composes Operations' side-effect-free run plan, Accounting's dry-run event projection and #377's
/// issued-statement matcher. It is a separate read from the capability-stamped run preview by design.
/// </summary>
internal sealed class GetRunPreviewIssuedCoverageHandler(
    RunEngine engine,
    ISender sender,
    AppDbContext db,
    IStatementNames names,
    TimeProvider clock) : IQueryHandler<GetRunPreviewIssuedCoverage, RunPreviewIssuedCoverageResponse>
{
    public async Task<RunPreviewIssuedCoverageResponse> Handle(
        GetRunPreviewIssuedCoverage query,
        CancellationToken ct)
    {
        var periodNow = clock.GetUtcNow();
        var runType = RunTypeRoute.Parse(query.Type);
        var period = new RunPeriod(query.Year ?? periodNow.Year, query.Month ?? periodNow.Month);
        var candidates = await engine.EligibleTargetsAsync(runType, period, ct);
        if (candidates.Count == 0)
        {
            return new RunPreviewIssuedCoverageResponse([]);
        }

        var candidateOwnerIds = candidates.Select(candidate => candidate.OwnerId).Distinct().ToArray();
        var runPeriodIndex = PeriodIndex(period.Year, period.Month);
        var issued = await db.Set<StatementArtifact>()
            .Where(artifact => candidateOwnerIds.Contains(artifact.OwnerId)
                && artifact.Basis != null
                && artifact.EndingBalance != null
                && artifact.AsOf != null
                && ((artifact.PeriodYear * 12) + artifact.PeriodMonth - 1) >= runPeriodIndex)
            .Select(artifact => new IssuedStatementCoverage.IssuedStatement(
                artifact.OwnerId,
                artifact.PeriodYear,
                artifact.PeriodMonth,
                artifact.Basis!,
                artifact.PropertyId,
                DateTime.SpecifyKind(artifact.AsOf!.Value, DateTimeKind.Utc)))
            .ToListAsync(ct);

        // This is the optimization boundary: no strategy plan, event mapping or Accounting dry run
        // happens unless one eligible target's owner has a qualifying immutable snapshot.
        if (issued.Count == 0)
        {
            return new RunPreviewIssuedCoverageResponse([]);
        }

        var plan = await engine.PlanEligibleAsync(runType, period, candidates, ct);
        if (plan.Count == 0)
        {
            return new RunPreviewIssuedCoverageResponse([]);
        }

        var eventSlots = plan
            .SelectMany(item => BatchPostingAdapter.ToEvents(item.Intent)
                .Select(accountingEvent => new EventSlot(item.TargetId, accountingEvent)))
            .ToArray();
        var prospective = await sender.Query(
            new GetProspectiveOwnerEquityLines(eventSlots.Select(slot => slot.Event).ToArray()), ct);

        if (prospective.Count != eventSlots.Length)
        {
            throw new InvalidOperationException(
                "The Accounting run-event projection must return exactly one owner-equity line per event.");
        }

        var postedAt = clock.GetUtcNow().UtcDateTime;
        var linesByTarget = prospective
            .Select((line, index) => new
            {
                eventSlots[index].TargetId,
                Line = new OwnerEquityLine(
                    UuidV7.NewId(),
                    line.OwnerId,
                    line.PropertyId,
                    line.Basis,
                    line.EntryDate,
                    postedAt,
                    line.Amount),
            })
            .GroupBy(item => item.TargetId)
            .ToArray();

        var matchesByTarget = linesByTarget
            .SelectMany(group => IssuedStatementCoverage.Match(
                    group.Select(item => item.Line).ToArray(), issued)
                .Select(match => new { TargetId = group.Key, Match = match }))
            .ToArray();
        if (matchesByTarget.Length == 0)
        {
            return new RunPreviewIssuedCoverageResponse([]);
        }

        var ownerNames = await names.GetOwnerNamesAsync(ct);
        var propertyAddresses = await names.GetPropertyAddressesAsync(ct);
        return new RunPreviewIssuedCoverageResponse(matchesByTarget
            .Select(item => new RunPreviewIssuedCoverageRow(
                item.TargetId,
                item.Match.OwnerId,
                ownerNames.GetValueOrDefault(item.Match.OwnerId, "Unknown owner"),
                item.Match.Basis,
                item.Match.PropertyId,
                item.Match.PropertyId is { } propertyId
                    ? propertyAddresses.GetValueOrDefault(propertyId)
                    : null,
                item.Match.IssuedYear,
                item.Match.IssuedMonth))
            .OrderBy(row => row.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Basis, StringComparer.Ordinal)
            .ThenBy(row => row.PropertyAddress, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.TargetId)
            .ToArray());
    }

    private static int PeriodIndex(int year, int month) => (year * 12) + (month - 1);

    private sealed record EventSlot(Guid TargetId, AccountingEvent Event);
}
