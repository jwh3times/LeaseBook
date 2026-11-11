using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Modules.Accounting.Endpoints;

/// <summary>
/// The M4 reconciliation surface (P64/P71): start/finalize/unlock plus history and the immutable report.
/// All <c>RequirePMStaff</c> except <b>unlock</b>, which is <c>RequirePMAdmin</c> + reason. Domain
/// rejections (non-zero difference, locked/finalized state) flow to the host's exception handler.
/// </summary>
public sealed class ReconciliationEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounting/reconciliations")
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Accounting");

        group.MapPost("/",
                async (StartReconciliation body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body, ct)))
            .Produces<ReconciliationView>();

        // The body is optional: with no lines named, every cleared, unreconciled line dated through the
        // month's last day is assigned (#656).
        group.MapPost("/{id:guid}/finalize",
                async (Guid id, FinalizeReconciliationRequest? body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(new FinalizeReconciliation(id, body?.AssignedLineIds), ct)))
            .Produces<ReconciliationView>();

        group.MapPost("/{id:guid}/unlock",
                async (Guid id, UnlockReconciliation body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body with { ReconciliationId = id }, ct)))
            .RequireAuthorization("RequirePMAdmin")
            .Produces<ReconciliationView>();

        group.MapGet("/",
                async (Guid? bankAccountId, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new GetReconciliationHistory(bankAccountId), ct)))
            .Produces<ReconciliationHistoryResponse>();

        group.MapGet("/{id:guid}/lines",
                async (Guid id, ISender sender, CancellationToken ct) =>
                {
                    var lines = await sender.Query(new GetReconciliationLines(id), ct);
                    return lines is null ? Results.NotFound() : Results.Ok(lines);
                })
            .Produces<ReconciliationLinesResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/report",
                async (Guid id, ISender sender, CancellationToken ct) =>
                {
                    var report = await sender.Query(new GetReconciliationReport(id), ct);
                    return report is null ? Results.NotFound() : Results.Ok(report);
                })
            .Produces<ReconciliationReportResponse>()
            .Produces(StatusCodes.Status404NotFound);
    }
}
