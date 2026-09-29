using System.Security.Claims;

namespace LeaseBook.Web.Portal;

public sealed class OwnerAccessService
{
    public Task<OwnerIdentity?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct) => throw new NotImplementedException();
    public Task GrantAsync(Guid userId, Guid ownerId, CancellationToken ct) => throw new NotImplementedException();
    public Task RevokeAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
}

public sealed record OwnerIdentity(Guid OwnerId, string DisplayName);
