using System.Security.Claims;
using LeaseBook.Modules.Reporting.Features.IssuedStatements;
using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LeaseBook.Web.Portal;

public sealed class OwnerRequirement : IAuthorizationRequirement;

/// <summary>Per-request result only: never placed in a cookie or a shared cache.</summary>
public sealed class CurrentOwner
{
    public OwnerIdentity? Identity { get; internal set; }
}

/// <summary>The single statement of who the owner persona is, shared by the policy and the handler.</summary>
public static class OwnerPersona
{
    public static bool Admits(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && user.IsInRole(Roles.Owner)
        && !user.IsInRole(Roles.PMAdmin) && !user.IsInRole(Roles.PMStaff) && !user.IsInRole(Roles.Tenant);
}

/// <summary>
/// Admits exactly the Owner persona with an active, non-system link in the request's organization. A
/// principal that also holds a staff or Tenant role is refused, so the persona stays exclusive.
/// </summary>
public sealed class OwnerAuthorizationHandler(OwnerAccessService access, CurrentOwner owner)
    : AuthorizationHandler<OwnerRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, OwnerRequirement requirement)
    {
        if (context.Resource is not HttpContext http || !OwnerPersona.Admits(context.User))
        {
            return;
        }
        owner.Identity = await access.ResolveAsync(context.User, http.RequestAborted);
        if (owner.Identity is not null) { context.Succeed(requirement); }
    }
}

/// <summary>Runs after OrgContextMiddleware; the early middleware policy checks the persona only.</summary>
public sealed class OwnerAccessFilter(IAuthorizationService authorization) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        var result = await authorization.AuthorizeAsync(http.User, http, new OwnerRequirement());
        return result.Succeeded ? await next(context) : TypedResults.Forbid();
    }
}

/// <summary>A dated disbursement to the owner. <c>Amount</c> is positive for money paid out and negative
/// for the reversal that returned it to the owner's trust balance.</summary>
public sealed record OwnerPortalDisbursement(DateOnly Date, decimal Amount, bool IsVoided, bool IsReversal);

/// <summary>One owner-equity movement: an allow-listed category, the property it concerns (only ever
/// one on the owner's own rows), the signed amount and the running balance after it.</summary>
public sealed record OwnerPortalActivityRow(
    DateOnly Date, string Category, string? PropertyAddress, decimal Amount, decimal Balance, bool IsVoided, bool IsReversal);

/// <summary><c>Balance</c> is the money held in trust for the owner — owner equity on <c>Basis</c>, the
/// organization's accounting basis — excluding tenant deposits.</summary>
public sealed record OwnerPortalSummary(
    string OwnerName, decimal Balance, string Basis,
    IReadOnlyList<OwnerPortalDisbursement> Disbursements, IReadOnlyList<OwnerPortalActivityRow> Activity);

/// <summary>One issued statement. <c>Scope</c> is the property address, or "All properties" for a
/// whole-owner statement. <c>Basis</c> and <c>EndingBalance</c> are null only on documents issued before
/// they were recorded.</summary>
public sealed record OwnerPortalStatement(
    Guid Id, int PeriodYear, int PeriodMonth, string? Basis, string Scope, decimal? EndingBalance, DateTime IssuedAt);

public sealed record OwnerPortalStatements(IReadOnlyList<OwnerPortalStatement> Statements);

/// <summary>
/// The read-only owner portal (#464, ADR-003). Routes take no owner, property or organization selector;
/// the only id accepted is an artifact id, which is checked against the resolved owner.
/// </summary>
public sealed class OwnerPortalEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/portal/owner").WithTags("Owner portal")
            .RequireAuthorization(AuthPolicies.RequireOwner).AddEndpointFilter<OwnerAccessFilter>();

        group.MapGet("/summary", async (CurrentOwner owner, OwnerPortalReader reader, CancellationToken ct) =>
            TypedResults.Ok(await reader.GetSummaryAsync(Resolved(owner), ct)))
            .Produces<OwnerPortalSummary>();

        group.MapGet("/statements", async (CurrentOwner owner, OwnerPortalReader reader, CancellationToken ct) =>
            TypedResults.Ok(await reader.GetStatementsAsync(Resolved(owner), ct)))
            .Produces<OwnerPortalStatements>();

        // Foreign, cross-org and nonexistent ids are one bare 404 (no existence oracle). A document the
        // owner was issued but whose bytes the store cannot return is a distinct, logged 503.
        group.MapGet("/statements/{artifactId:guid}/pdf",
            async Task<Results<FileContentHttpResult, NotFound, ProblemHttpResult>> (
                Guid artifactId, CurrentOwner owner, OwnerPortalReader reader, HttpContext httpContext,
                CancellationToken ct) =>
            {
                var document = await reader.GetDocumentAsync(Resolved(owner), artifactId, ct);
                return document.Status switch
                {
                    IssuedStatementDocumentStatus.Available => TypedResults.File(document.Content!, "application/pdf"),
                    IssuedStatementDocumentStatus.Unavailable => ProblemResults.TypedProblem(
                        httpContext,
                        code: "statement_document_unavailable",
                        detail: "This statement was issued, but its document cannot be retrieved right now. "
                            + "Contact your property manager.",
                        status: StatusCodes.Status503ServiceUnavailable),
                    _ => TypedResults.NotFound(),
                };
            })
            // The union supplies the 404/problem metadata; a file result declares none of its own.
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf");
    }

    private static OwnerIdentity Resolved(CurrentOwner owner) =>
        owner.Identity ?? throw new InvalidOperationException("Owner authorization must precede dispatch.");
}
