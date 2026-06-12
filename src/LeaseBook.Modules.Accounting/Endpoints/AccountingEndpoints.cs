using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Modules.Accounting.Endpoints;

/// <summary>
/// The accounting module's read endpoints (§C.6) — the first real org-scoped read surface, replacing
/// the M0 diagnostics probe. All GET, behind the RequirePMStaff policy; each lambda stays thin
/// (bind → dispatch via <see cref="ISender"/> → TypedResults). Org scope and RLS come from the request
/// middleware. M1 exposes no write endpoints.
/// </summary>
public sealed class AccountingEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        // The policy is registered in the host as AuthPolicies.RequirePMStaff (= "RequirePMStaff").
        var group = app.MapGroup("/api/accounting")
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Accounting");

        group.MapGet("/tenants/{tenantId:guid}/ledger",
                async (Guid tenantId, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new GetTenantLedger(tenantId), ct)))
            .Produces<TenantLedgerResponse>();

        group.MapGet("/owners/balances",
                async (ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new GetOwnerBalances(), ct)))
            .Produces<OwnerBalancesResponse>();

        group.MapGet("/owners/{ownerId:guid}/ledger",
                async (Guid ownerId, string? basis, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new GetOwnerLedger(ownerId, basis ?? "cash"), ct)))
            .Produces<OwnerLedgerResponse>();

        group.MapGet("/banks/balances",
                async (ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new GetBankBalances(), ct)))
            .Produces<BankBalancesResponse>();

        group.MapGet("/deposits",
                async (ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new GetDepositRegister(), ct)))
            .Produces<DepositRegisterResponse>();

        group.MapGet("/trust-equation",
                async (ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new GetTrustEquation(), ct)))
            .Produces<TrustEquationResponse>();
    }
}
