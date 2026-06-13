using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Shared;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.Modules.Directory.Endpoints;

/// <summary>
/// The directory read/CRUD surface (§C.3): paged lists + details for owners/properties/units/tenants and
/// create/update for each (CRUD is staff-level; only settings writes are admin, §C.4). Thin lambdas:
/// bind → dispatch via <see cref="ISender"/> → <c>TypedResults</c>. Financial figures are merged inside
/// the queries through the Accounting ports (P49). No delete in M2; no journal writes (that is M3).
/// </summary>
public sealed class DirectoryEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/directory")
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Directory");

        MapOwners(group);
        MapProperties(group);
        MapUnits(group);
        MapTenants(group);
        MapLeases(group);
    }

    private static void MapOwners(RouteGroupBuilder group)
    {
        group.MapGet("/owners",
                async (int? page, int? pageSize, string? q, string? sort, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new ListOwners(page, pageSize, q, sort), ct)))
            .Produces<PagedResponse<OwnerListRow>>();

        group.MapGet("/owners/{id:guid}",
                async Task<Results<Ok<OwnerDetail>, NotFound>> (Guid id, ISender sender, CancellationToken ct) =>
                    await sender.Query(new GetOwnerDetail(id), ct) is { } d ? TypedResults.Ok(d) : TypedResults.NotFound())
            .Produces<OwnerDetail>();

        group.MapPost("/owners",
                async (CreateOwner body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(new CreatedId(await sender.Send(body, ct))));

        group.MapPut("/owners/{id:guid}",
                async Task<Results<NoContent, NotFound>> (Guid id, UpdateOwner body, ISender sender, CancellationToken ct) =>
                    await sender.Send(body with { Id = id }, ct) ? TypedResults.NoContent() : TypedResults.NotFound());
    }

    private static void MapProperties(RouteGroupBuilder group)
    {
        group.MapGet("/properties",
                async (int? page, int? pageSize, string? q, string? sort, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new ListProperties(page, pageSize, q, sort), ct)))
            .Produces<PagedResponse<PropertyListRow>>();

        group.MapGet("/properties/{id:guid}",
                async Task<Results<Ok<PropertyDetail>, NotFound>> (Guid id, ISender sender, CancellationToken ct) =>
                    await sender.Query(new GetPropertyDetail(id), ct) is { } d ? TypedResults.Ok(d) : TypedResults.NotFound())
            .Produces<PropertyDetail>();

        group.MapPost("/properties",
                async (CreateProperty body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(new CreatedId(await sender.Send(body, ct))));

        group.MapPut("/properties/{id:guid}",
                async Task<Results<NoContent, NotFound>> (Guid id, UpdateProperty body, ISender sender, CancellationToken ct) =>
                    await sender.Send(body with { Id = id }, ct) ? TypedResults.NoContent() : TypedResults.NotFound());
    }

    private static void MapUnits(RouteGroupBuilder group)
    {
        group.MapGet("/units",
                async (Guid propertyId, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new ListUnits(propertyId), ct)))
            .Produces<IReadOnlyList<UnitRow>>();

        group.MapPost("/units",
                async (CreateUnit body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(new CreatedId(await sender.Send(body, ct))));

        group.MapPut("/units/{id:guid}",
                async Task<Results<NoContent, NotFound>> (Guid id, UpdateUnit body, ISender sender, CancellationToken ct) =>
                    await sender.Send(body with { Id = id }, ct) ? TypedResults.NoContent() : TypedResults.NotFound());
    }

    private static void MapTenants(RouteGroupBuilder group)
    {
        group.MapGet("/tenants",
                async (int? page, int? pageSize, string? q, string? sort, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(await sender.Query(new ListTenants(page, pageSize, q, sort), ct)))
            .Produces<PagedResponse<TenantListRow>>();

        group.MapGet("/tenants/{id:guid}",
                async Task<Results<Ok<TenantDetail>, NotFound>> (Guid id, ISender sender, CancellationToken ct) =>
                    await sender.Query(new GetTenantDetail(id), ct) is { } d ? TypedResults.Ok(d) : TypedResults.NotFound())
            .Produces<TenantDetail>();

        group.MapPost("/tenants",
                async (CreateTenant body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(new CreatedId(await sender.Send(body, ct))));

        group.MapPut("/tenants/{id:guid}",
                async Task<Results<NoContent, NotFound>> (Guid id, UpdateTenant body, ISender sender, CancellationToken ct) =>
                    await sender.Send(body with { Id = id }, ct) ? TypedResults.NoContent() : TypedResults.NotFound());
    }

    private static void MapLeases(RouteGroupBuilder group)
    {
        group.MapPost("/leases",
                async (CreateLease body, ISender sender, CancellationToken ct) =>
                    TypedResults.Ok(new CreatedId(await sender.Send(body, ct))));

        group.MapPut("/leases/{id:guid}",
                async Task<Results<NoContent, NotFound>> (Guid id, UpdateLease body, ISender sender, CancellationToken ct) =>
                    await sender.Send(body with { Id = id }, ct) ? TypedResults.NoContent() : TypedResults.NotFound());
    }
}

/// <summary>Response for a create: the new entity's id (the SPA navigates to it).</summary>
public sealed record CreatedId(Guid Id);
