using LeaseBook.SharedKernel.Endpoints;
using LeaseBook.Web.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Web.Endpoints;

/// <summary>
/// The host-composed dashboard endpoint (§C.6). <c>GET /api/dashboard</c> (RequirePMStaff); the lambda
/// stays thin — composition is <see cref="DashboardService"/>'s job (P45). Host-owned because the
/// dashboard legitimately reads across modules (the composition root), via <c>ISender</c>.
/// </summary>
public sealed class DashboardEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dashboard",
                async (DashboardService dashboard, CancellationToken ct) =>
                    TypedResults.Ok(await dashboard.ComposeAsync(ct)))
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Dashboard")
            .Produces<DashboardResponse>();
    }
}
