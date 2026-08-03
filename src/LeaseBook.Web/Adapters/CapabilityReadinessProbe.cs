using LeaseBook.Modules.Capabilities.Caching;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Establishes at startup that this replica can reach the capability seam, flipping
/// <see cref="CapabilityCache.IsPopulated"/> so the readiness probe can pass.
/// <para>
/// <b>Why this exists at all.</b> Capability sets are keyed per <c>(org, user)</c> and only load on
/// demand, so nothing populates the cache until a request arrives. Gating readiness on "something is
/// cached" would therefore deadlock every deploy: a fresh replica is not-ready, so it receives no
/// traffic, so it never resolves a capability, so it stays not-ready. Forever. There is no warm-up
/// that could break the cycle — with no org in hand there is no set to warm.
/// </para>
/// <para>
/// So readiness means <b>reachable</b>, proven by one trivial platform-scoped read that depends on no
/// inbound traffic. That keeps the property that actually matters (never serve from a seam that
/// cannot reach its data) without tying it to a chicken-and-egg.
/// </para>
/// <para>
/// It retries with backoff rather than giving up, because a database that is merely slow to accept
/// connections at boot — an ordinary occurrence on a cold Container Apps revision — must not pin the
/// replica out of rotation permanently. It stops at the first success: reachability is not re-proven,
/// and a later blip is handled by serving last-known-good rather than by flapping readiness.
/// </para>
/// </summary>
public sealed class CapabilityReadinessProbe(
    CapabilityCache cache,
    ILogger<CapabilityReadinessProbe> logger) : BackgroundService
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);

    private long _attempts;

    /// <summary>Probe attempts made, successful or not. Diagnostic only.</summary>
    public long Attempts => Interlocked.Read(ref _attempts);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not hold up host startup: BackgroundService awaits ExecuteAsync up to its first yield.
        await Task.Yield();

        var backoff = InitialBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _attempts);

            // ProbeAsync swallows and reports rather than throwing, which matters here: an escape
            // from ExecuteAsync would stop the host, since BackgroundServiceExceptionBehavior
            // defaults to StopHost.
            if (await cache.ProbeAsync(stoppingToken))
            {
                logger.LogInformation(
                    "The capability seam is reachable after {Attempts} attempt(s); this replica is ready.",
                    Attempts);
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
