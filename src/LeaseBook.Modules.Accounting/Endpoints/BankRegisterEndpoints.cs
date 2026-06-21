using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Modules.Accounting.Endpoints;

/// <summary>
/// The M4 bank-register surface (§B.4 / P71). The register read is a filterable/paginated projection of
/// the journal on one bank account; the write surface adds the three bank-adjustment templates and the
/// clearance command. All <c>RequirePMStaff</c>, thin (bind → dispatch → <c>TypedResults</c>); domain
/// rejections flow to the host's <c>AccountingExceptionHandler</c>. (<c>GET /banks/balances</c> already
/// lives in <see cref="AccountingEndpoints"/>.)
/// </summary>
public sealed class BankRegisterEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounting")
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Accounting");

        group.MapGet("/banks/{bankAccountId:guid}/register",
                async (
                    Guid bankAccountId, Guid? property, string? type, DateOnly? from, DateOnly? to,
                    string? search, int? page, int? pageSize, ISender sender, CancellationToken ct) =>
                {
                    var query = new GetBankRegister(
                        bankAccountId, property, ParseType(type), from, to, search, page ?? 1, pageSize ?? 50);
                    return TypedResults.Ok(await sender.Query(query, ct));
                })
            .Produces<RegisterResponse>();

        group.MapPost("/banks/{bankAccountId:guid}/adjustments",
                async (Guid bankAccountId, RecordBankAdjustment body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body with { BankAccountId = bankAccountId }, ct)))
            .Produces<PostResult>();

        group.MapPost("/banks/clearances",
                async (ApplyClearances body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Send(body, ct)))
            .Produces<ClearancesResult>();
    }

    private static RegisterTypeFilter ParseType(string? type) => type?.ToLowerInvariant() switch
    {
        "deposits" => RegisterTypeFilter.Deposits,
        "withdrawals" => RegisterTypeFilter.Withdrawals,
        _ => RegisterTypeFilter.All,
    };
}
