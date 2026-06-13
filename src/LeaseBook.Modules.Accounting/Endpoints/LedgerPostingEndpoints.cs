using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Modules.Accounting.Endpoints;

/// <summary>
/// The M3 ledger <b>write</b> surface (§C.1) — the first write endpoints in the product. Posting and
/// void are <c>RequirePMStaff</c> (money entry is staff-level, P53); each lambda stays thin (bind →
/// dispatch the command → <c>TypedResults</c>). Owner/property/unit are never in the body — the command
/// resolves them from the tenant's active lease (P58). Domain rejections flow to the host's
/// <c>AccountingExceptionHandler</c> (422/409, §C.5). The focused ledger CSV (P55) lives here too.
/// All commands route through the existing engine — M3 adds no journal write path (M3-E1).
/// </summary>
public sealed class LedgerPostingEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounting")
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Accounting");

        // The route's tenant id is authoritative; it overrides whatever the body carries (house pattern,
        // mirroring Directory's `body with { Id = id }`).
        group.MapPost("/tenants/{tenantId:guid}/payments",
                async (Guid tenantId, RecordPayment body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body with { TenantId = tenantId }, ct)))
            .Produces<PostResult>();

        group.MapPost("/tenants/{tenantId:guid}/charges",
                async (Guid tenantId, AddCharge body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body with { TenantId = tenantId }, ct)))
            .Produces<PostResult>();

        group.MapPost("/tenants/{tenantId:guid}/credits",
                async (Guid tenantId, IssueCredit body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body with { TenantId = tenantId }, ct)))
            .Produces<PostResult>();

        group.MapPost("/tenants/{tenantId:guid}/deposits",
                async (Guid tenantId, CollectDeposit body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body with { TenantId = tenantId }, ct)))
            .Produces<PostResult>();

        group.MapPost("/tenants/{tenantId:guid}/prepayments",
                async (Guid tenantId, CollectPrepayment body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body with { TenantId = tenantId }, ct)))
            .Produces<PostResult>();

        group.MapPost("/tenants/{tenantId:guid}/deposit-applications",
                async (Guid tenantId, ApplyDeposit body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body with { TenantId = tenantId }, ct)))
            .Produces<PostResult>();

        group.MapPost("/tenants/{tenantId:guid}/prepayment-applications",
                async (Guid tenantId, ApplyPrepayment body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body with { TenantId = tenantId }, ct)))
            .Produces<PostResult>();

        group.MapPost("/entries/{entryId:guid}/void",
                async (Guid entryId, VoidEntry body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body with { EntryId = entryId }, ct)))
            .Produces<PostResult>();

        // Focused ledger CSV (P55): mirrors the on-screen ledger, reusing the existing projection.
        group.MapGet("/tenants/{tenantId:guid}/ledger.csv",
                async (Guid tenantId, ISender sender, CancellationToken ct) =>
                {
                    var ledger = await sender.Query(new GetTenantLedger(tenantId), ct);
                    return Results.File(TenantLedgerCsv.Write(ledger), "text/csv", $"tenant-{tenantId:N}-ledger.csv");
                })
            .Produces(StatusCodes.Status200OK, contentType: "text/csv");
    }
}
