using Microsoft.AspNetCore.Routing;

namespace LeaseBook.SharedKernel.Endpoints;

/// <summary>
/// Implemented once per module to register that module's minimal-API endpoints (P22). The host
/// discovers and invokes these via <c>MapModuleEndpoints</c>. Endpoints stay thin: bind → dispatch
/// via <see cref="Cqrs.ISender"/> → map to TypedResults (§C.8).
/// </summary>
public interface IEndpointModule
{
    void MapEndpoints(IEndpointRouteBuilder app);
}
