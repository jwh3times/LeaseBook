using System.Net.Sockets;
using Npgsql;

namespace LeaseBook.Web.Persistence;

/// <summary>
/// Tells "the server could not be reached" apart from "the server answered and said no".
/// <para>
/// <b>One implementation, deliberately.</b> Two startup paths now ride out a database outage rather
/// than killing the process — <c>CapabilityRegistryValidator.ValidateAsync</c> and
/// <c>RoleSeeder.TryEnsureRolesAsync</c> — and the distinction they both depend on is subtle enough
/// that a second copy would drift into the naive form described below and silently stop firing. It
/// lives in <c>Persistence</c> because it is a fact about the host's Npgsql driver, not about either
/// caller. The body is unchanged from the version <c>CapabilityRegistryValidator</c> proved against a
/// genuinely dead endpoint; it was moved, not rewritten.
/// </para>
/// </summary>
public static class DatabaseReachability
{
    /// <summary>
    /// True when the failure is "the server could not be reached", false when it is "the server
    /// answered and said no".
    /// </summary>
    /// <remarks>
    /// <b>The chain has to be walked, not the top frame matched.</b> EF Core wraps a transient
    /// provider failure in an <see cref="InvalidOperationException"/> reading "An exception has been
    /// raised that is likely due to a transient failure", with the real <see cref="NpgsqlException"/>
    /// underneath. A filter written against the exception type actually thrown therefore never fires
    /// against a live unreachable database, only against a hand-thrown test double — which is
    /// precisely the trap <c>An_unreachable_database_does_not_stop_the_host_from_binding</c> caught.
    /// Identity's role store adds one more layer of the same shape (a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> around the provider failure), and
    /// the walk handles it for the same reason.
    /// <para>
    /// <see cref="PostgresException"/> is checked BEFORE its <see cref="NpgsqlException"/> base and
    /// returns false: a server-side error such as a missing table or a revoked grant means the
    /// connection worked perfectly and the deployment is broken. That must keep surfacing rather than
    /// being ridden out as an outage — for role seeding especially, where riding it out would leave a
    /// replica not-ready forever instead of failing loudly on a bad grant.
    /// </para>
    /// </remarks>
    public static bool IsUnreachable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case PostgresException:
                    return false;
                case NpgsqlException or SocketException or TimeoutException:
                    return true;
            }
        }

        return false;
    }
}
