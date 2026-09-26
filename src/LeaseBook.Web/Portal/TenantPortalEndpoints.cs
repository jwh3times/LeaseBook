using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Authorization;

namespace LeaseBook.Web.Portal;

public sealed class ResidentRequirement : IAuthorizationRequirement;

/// <summary>Per-request result only: never placed in a cookie or a shared cache.</summary>
public sealed class CurrentResident
{
    public ResidentIdentity? Identity { get; internal set; }
}

public sealed class ResidentAuthorizationHandler(ResidentAccessService access, CurrentResident resident)
    : AuthorizationHandler<ResidentRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ResidentRequirement requirement)
    {
        if (context.Resource is not HttpContext http || context.User.Identity?.IsAuthenticated != true
            || !context.User.IsInRole(Roles.Tenant)
            || context.User.IsInRole(Roles.PMAdmin) || context.User.IsInRole(Roles.PMStaff) || context.User.IsInRole(Roles.Owner))
        {
            return;
        }
        resident.Identity = await access.ResolveAsync(context.User, http.RequestAborted);
        if (resident.Identity is not null) { context.Succeed(requirement); }
    }
}

/// <summary>Runs after OrgContextMiddleware; the early middleware policy checks the persona only.</summary>
public sealed class ResidentAccessFilter(IAuthorizationService authorization) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        var result = await authorization.AuthorizeAsync(http.User, http, new ResidentRequirement());
        return result.Succeeded ? await next(context) : TypedResults.Forbid();
    }
}

public sealed record ResidentLedgerRow(
    DateOnly Date, string Category, decimal Charge, decimal Payment, decimal Balance, bool IsVoided, bool IsReversal);
public sealed record ResidentLedgerResponse(string ResidentName, decimal Balance, IReadOnlyList<ResidentLedgerRow> Rows);

public sealed class TenantPortalEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/portal/tenant").WithTags("Tenant portal")
            .RequireAuthorization(AuthPolicies.RequireTenant).AddEndpointFilter<ResidentAccessFilter>();
        group.MapGet("/ledger", async (CurrentResident resident, ISender sender, CancellationToken ct) =>
        {
            var identity = resident.Identity ?? throw new InvalidOperationException("Resident authorization must precede dispatch.");
            var ledger = await sender.Query(new GetTenantLedger(identity.TenantId), ct);
            return TypedResults.Ok(new ResidentLedgerResponse(identity.DisplayName, ledger.Balance,
                ledger.Rows.Select(r => new ResidentLedgerRow(r.Date, ResidentCategory(r), r.Charge, r.Payment,
                    r.Balance, r.IsVoided, r.ReversesEntryId is not null)).ToArray()));
        }).Produces<ResidentLedgerResponse>();
    }

    // Do not expose Accounting's fallback event-type names as resident copy.
    private static string ResidentCategory(TenantLedgerEntry row) => row.ReversesEntryId is not null
        ? "Reversal"
        : row.Category switch
        {
            "Rent" or "Late Fee" or "Maintenance" or "Fee" or "Credit" or "Payment" or "Prepayment" => row.Category,
            _ => "Adjustment",
        };
}
