namespace LeaseBook.Web.Auth;

/// <summary>
/// Keeps retrying role seeding until it succeeds, for the boot where the database was unreachable.
/// <para>
/// <b>Why it exists.</b> <c>Program.cs</c> still seeds roles synchronously before <c>app.Run()</c>,
/// and against a reachable database that call succeeds and this service exits on its first line — the
/// ordinary path is unchanged, including for the CLI verbs, the test host and Docker. It matters only
/// on the boot where <see cref="RoleSeeder.TryEnsureRolesAsync"/> reported an unreachable server. That
/// boot now binds a port instead of dying, which means something has to finish the job later: without
/// a retry, a replica that came up during an outage would stay role-less and not-ready for its entire
/// life, and the guard would have converted a crash loop into a permanently useless replica.
/// </para>
/// <para>
/// <b>A sibling of <c>CapabilityReadinessProbe</c>, not an extension of it.</b> They share a shape —
/// background retry, capped backoff, exit at first success — and nothing else. One proves a read seam
/// is reachable; this one performs an Identity write and can fail for reasons (a bad grant, a failed
/// validator) that have no counterpart there. Keeping them apart is what lets the readiness endpoint
/// name which precondition is missing, and stops a change to either failure policy from silently
/// moving the other.
/// </para>
/// <para>
/// <b>Only an unreachable database is retried.</b> Anything else — a missing table, a revoked grant, a
/// role that could not be created — escapes <see cref="ExecuteAsync"/>, and with the default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c> that stops the host. That is deliberate and is
/// exactly what the synchronous path would have done with the same fault: a deployment defect must
/// stay loud. Retrying it instead would leave a replica logging into the void forever, since readiness
/// failures remove a replica from rotation but never restart it.
/// </para>
/// </summary>
public sealed class RoleSeedingProbe(
    IServiceProvider services,
    RoleSeedingState state,
    ILogger<RoleSeedingProbe> logger) : BackgroundService
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not hold up host startup: BackgroundService awaits ExecuteAsync up to its first yield.
        await Task.Yield();

        if (state.IsSeeded)
        {
            // The synchronous attempt in Program.cs already succeeded, which is every ordinary boot.
            return;
        }

        var backoff = InitialBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            var attempt = state.RecordAttempt();

            // TryEnsureRolesAsync swallows an unreachable database and rethrows everything else; see
            // the class remarks for why the rethrow is allowed to stop the host.
            if (await RoleSeeder.TryEnsureRolesAsync(services, stoppingToken))
            {
                state.MarkSeeded();
                logger.LogInformation(
                    "The fixed roles are seeded after {Attempts} retry attempt(s); this replica can serve " +
                    "authenticated traffic.",
                    attempt);
                return;
            }

            try
            {
                await Task.Delay(backoff, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }
}
