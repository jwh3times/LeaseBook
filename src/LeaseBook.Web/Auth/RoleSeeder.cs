using LeaseBook.SharedKernel;
using LeaseBook.Web.Persistence;
using Microsoft.AspNetCore.Identity;

namespace LeaseBook.Web.Auth;

/// <summary>
/// Ensures the fixed <see cref="Roles"/> exist. Idempotent and run at startup so the seeder (WP-09)
/// and authorization policies always have their roles. Roles live in identity-class tables (no RLS),
/// so this needs no organization context.
/// <para>
/// <b>Two entry points, and the difference is the whole point.</b> <see cref="EnsureRolesAsync"/>
/// throws on anything that goes wrong and is what the CLI seeders call: an operator running
/// <c>seed</c> against a database they cannot reach must be told so, immediately and loudly.
/// <see cref="TryEnsureRolesAsync"/> rides out an unreachable server and reports it, and is what the
/// host's boot path calls, so a replica coming up during a database outage still binds a port. Every
/// other failure — a missing table, a revoked grant, a role that could not be created — still throws
/// on both paths.
/// </para>
/// <para>
/// <b>Riding out an outage is only safe because readiness gates on the result.</b> Skipping role
/// seeding on its own would be worse than crashing: sign-in and every role-based policy need these
/// four rows, so a replica that swallowed the failure and passed readiness would enter rotation and
/// accept traffic it cannot serve — a silent partial outage in place of a loud crash loop. The half
/// that makes it safe is <see cref="RoleSeedingState"/>, which <see cref="RoleSeedingProbe"/> keeps
/// retrying and <c>RoleSeedingReadinessCheck</c> reports (ADR-028 §9). Do not call
/// <see cref="TryEnsureRolesAsync"/> from anywhere that does not gate on that state.
/// </para>
/// </summary>
public static class RoleSeeder
{
    /// <summary>
    /// Creates any missing role, throwing on any failure at all — including an unreachable database.
    /// </summary>
    public static async Task EnsureRolesAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        foreach (var role in Roles.All)
        {
            ct.ThrowIfCancellationRequested();

            if (await roleManager.RoleExistsAsync(role))
            {
                continue;
            }

            IdentityResult result;
            try
            {
                result = await roleManager.CreateAsync(new IdentityRole<Guid>(role) { Id = UuidV7.NewId() });
            }
            catch (Exception ex) when (!DatabaseReachability.IsUnreachable(ex))
            {
                // Losing a race is success. Production scales to five replicas and they boot together,
                // so two can both see the role missing and both insert; the loser takes a unique
                // violation on AspNetRoles' normalized-name index. The post-condition this method
                // promises is "the role exists", not "we created it". An unreachable database is
                // excluded from the filter so it reaches the caller's own handling unchanged.
                if (await roleManager.RoleExistsAsync(role))
                {
                    continue;
                }

                throw;
            }

            // The IdentityResult was previously discarded, which is the quiet version of the failure
            // this whole boot path exists to make loud: a validator rejection returns a failed result
            // rather than throwing, so the role would simply not exist and the host would carry on and
            // report itself ready. Re-check before believing the failure, for the race above.
            if (!result.Succeeded && !await roleManager.RoleExistsAsync(role))
            {
                throw new InvalidOperationException(
                    $"Could not create the '{role}' role, so sign-in and every role-based policy would " +
                    $"be broken on this replica: " +
                    $"{string.Join("; ", result.Errors.Select(error => $"{error.Code}: {error.Description}"))}");
            }
        }
    }

    /// <summary>
    /// <see cref="EnsureRolesAsync"/>, except that an unreachable database is logged and reported as
    /// <see langword="false"/> instead of thrown — so the host binds and the readiness gate, not the
    /// process exit code, keeps the replica out of rotation.
    /// </summary>
    /// <returns><see langword="true"/> when all four roles now exist.</returns>
    /// <remarks>
    /// Narrow by construction: only <see cref="DatabaseReachability.IsUnreachable"/> is swallowed. A
    /// missing table or a revoked grant is a deployment defect and still throws, because the answer to
    /// it is a fix and a redeploy, not a replica quietly waiting forever for a recovery that will
    /// never come.
    /// </remarks>
    public static async Task<bool> TryEnsureRolesAsync(
        IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            await EnsureRolesAsync(services, ct);
            return true;
        }
        catch (Exception ex) when (DatabaseReachability.IsUnreachable(ex))
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ILogger<RoleSeederMarker>>().LogError(
                ex,
                "Could not reach the database to seed the {RoleCount} fixed roles; continuing rather than " +
                "failing here, so a web host still binds a port. A web host reports NOT READY at " +
                "/api/health/ready and takes no traffic until RoleSeedingProbe's retries succeed. A CLI " +
                "verb has no readiness gate and simply carries on: `seed` reseeds roles itself and will " +
                "fail on this same outage, and no other verb needs them.",
                Roles.All.Length);

            return false;
        }
    }
}

/// <summary>
/// Log-category anchor for <see cref="RoleSeeder"/>, which is static and so cannot be a generic
/// argument to <see cref="ILogger{TCategoryName}"/>. Named rather than reusing an unrelated type so
/// the category an operator filters on says what produced the entry.
/// </summary>
public sealed class RoleSeederMarker
{
}
