using LeaseBook.SharedKernel;
using Microsoft.AspNetCore.Identity;

namespace LeaseBook.Web.Auth;

/// <summary>
/// Ensures the fixed <see cref="Roles"/> exist. Idempotent and run at startup so the seeder (WP-09)
/// and authorization policies always have their roles. Roles live in identity-class tables (no RLS),
/// so this needs no org context.
/// </summary>
public static class RoleSeeder
{
    public static async Task EnsureRolesAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole<Guid>(role) { Id = UuidV7.NewId() });
            }
        }
    }
}
