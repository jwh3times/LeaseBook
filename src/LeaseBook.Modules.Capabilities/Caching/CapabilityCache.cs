using System.Collections.Concurrent;
using LeaseBook.Modules.Capabilities.Contracts;
using LeaseBook.Modules.Capabilities.Resolution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LeaseBook.Modules.Capabilities.Caching;

/// <summary>
/// Per-replica cache of resolved capability sets, keyed by <c>(org, user)</c>.
/// <para>
/// <b>The 30-second TTL is the correctness floor; <c>NOTIFY</c> is a latency optimization.</b> A
/// missed, dropped or never-delivered notification must still self-heal within the TTL, so nothing
/// here may depend on the listener being alive. <c>CapabilityPropagationTests</c> pins both halves
/// separately.
/// </para>
/// <para>
/// <b>Lifetime.</b> Singleton — this is per-replica state. Out-of-band callers use the
/// <see cref="IServiceScopeFactory"/> platform loader; request callers supply a scoped ambient loader
/// through <c>CapabilityGate</c>. The singleton never holds <see cref="IPlatformScope"/> or a
/// <c>DbContext</c>, either of which would be a captive scoped dependency: validation would reject it,
/// or worse, it would quietly reuse a disposed context.
/// </para>
/// <para>
/// <b>Failure policy.</b> After a successful load for a key, a failing refresh serves last-known-good
/// and logs — a database blip must not disable paid features. Before the first load there is nothing
/// to serve, so the exception propagates and <see cref="IsPopulated"/> stays false; the readiness
/// probe gates on that, because serving traffic from an unpopulated seam is indistinguishable from
/// serving traffic with every capability off.
/// </para>
/// </summary>
public sealed class CapabilityCache(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<CapabilityCache> logger)
{
    /// <summary>
    /// The correctness floor: the longest a missed notification can leave this replica stale. Public
    /// because it is part of the seam's contract — the listener names it when it warns that
    /// propagation has degraded to the TTL, and callers reasoning about staleness need the number
    /// rather than a copy of it.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a failed refresh suppresses the next attempt for that key. Without it, a database
    /// outage turns last-known-good into a serialized queue of full connect timeouts: every caller
    /// takes the lock, waits out the timeout, and hands over — so the Nth caller waits N timeouts for
    /// a set it already had. Short enough that recovery is still prompt.
    /// </summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<CacheKey, Entry> _entries = new();

    // Per-KEY refresh locks, not one process-wide lock. A NOTIFY invalidates every key at once, so a
    // single lock would make a request for org B queue behind a refresh for org A — at the load
    // fixture's 300 orgs that is a p95 cliff against the budget in docs/perf.md. Growth is bounded by
    // the same key set _entries already holds, so this adds no new unbounded allocation class.
    private readonly ConcurrentDictionary<CacheKey, SemaphoreSlim> _refreshLocks = new();

    // Bumped by Invalidate(). An entry stamped with an older generation is stale regardless of age,
    // which is how a notification short-circuits the TTL without touching any timestamp.
    private long _generation;

    private long _coldLoads;
    private long _notificationRefreshes;
    private long _ttlRefreshes;
    private long _staleServedAfterFailure;

    /// <summary>
    /// <b>"The seam is reachable", not "some org's set is cached."</b> The readiness probe gates on
    /// this, and the distinction is the difference between a rolling deploy and an outage: sets are
    /// keyed per <c>(org, user)</c> and only ever load on demand, so a flag meaning "something is
    /// cached" would leave a fresh replica not-ready → receiving no traffic → never resolving
    /// anything → not-ready forever. There is no warm-up that could break the cycle, because there is
    /// no org to warm up for.
    /// <para>
    /// It is therefore set by a startup reachability read under platform scope (see
    /// <see cref="ProbeAsync"/>), which still enforces the property that actually matters — never
    /// serve traffic from a seam that cannot reach its data — and by any successful load thereafter.
    /// It is never cleared: once reachability is proven, a later blip is handled by serving
    /// last-known-good, and flapping readiness would evict a replica that is still answering
    /// correctly.
    /// </para>
    /// <para>
    /// <b>Volatile</b>, because it is written from the readiness probe's background task and read from
    /// request threads through the health check. Without it the JIT is free to hoist the read, and a
    /// replica could report not-ready indefinitely after the probe had already succeeded.
    /// </para>
    /// </summary>
    public bool IsPopulated => _isPopulated;

    private volatile bool _isPopulated;

    /// <summary>First-ever load of a key — neither an expiry nor a notification.</summary>
    public long ColdLoads => Interlocked.Read(ref _coldLoads);

    /// <summary>
    /// Refreshes driven by a <c>NOTIFY</c>. Counted separately from <see cref="TtlRefreshes"/> because
    /// self-healing hides a dead listener perfectly: with the notify path broken the system still
    /// converges, just 30 seconds late, and nothing else in the process would ever say so. A host
    /// whose notification count stays at zero while flags are being flipped has lost its listener.
    /// </summary>
    public long NotificationRefreshes => Interlocked.Read(ref _notificationRefreshes);

    /// <summary>Refreshes driven by TTL expiry — the correctness floor doing its job.</summary>
    public long TtlRefreshes => Interlocked.Read(ref _ttlRefreshes);

    /// <summary>Times a failed refresh fell back to last-known-good.</summary>
    public long StaleServedAfterFailure => Interlocked.Read(ref _staleServedAfterFailure);

    /// <summary>
    /// The resolved set for this <c>(org, user)</c>, refreshing when the cached entry has expired or
    /// been invalidated. This out-of-band form opens a platform-scoped read and is intended for
    /// callers that do not already own an org transaction (readiness, propagation tests, and host
    /// infrastructure). Request code goes through <c>CapabilityGate</c>, which supplies its ambient
    /// loader to the overload below so a cache miss cannot consume a second pooled connection.
    /// </summary>
    public Task<CapabilitySet> GetAsync(Guid orgId, Guid? userId, CancellationToken ct = default) =>
        GetAsync(orgId, userId, token => LoadAsync(orgId, userId, token), ct);

    /// <summary>
    /// Resolves through the shared cache while letting a scoped caller perform a miss/refresh on its
    /// existing transaction. The delegate is used only after both freshness checks, under the
    /// per-key refresh lock; cache hits never touch it.
    /// </summary>
    internal async Task<CapabilitySet> GetAsync(
        Guid orgId,
        Guid? userId,
        Func<CancellationToken, Task<CapabilitySet>> loader,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(loader);

        var key = new CacheKey(orgId, userId);

        if (TryGetFresh(key, out var cached))
        {
            return cached;
        }

        var refreshLock = _refreshLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(ct);
        try
        {
            // Re-check: another caller may have refreshed this key while we queued.
            if (TryGetFresh(key, out cached))
            {
                return cached;
            }

            // Read the generation BEFORE the load, so a NOTIFY that lands mid-load leaves the new
            // entry stamped with the old generation and therefore already stale. Stamping with the
            // post-load value would swallow that notification entirely.
            var generation = Interlocked.Read(ref _generation);
            _entries.TryGetValue(key, out var stale);

            var reason =
                stale is null ? RefreshReason.ColdLoad
                : stale.Generation != generation ? RefreshReason.Notification
                : RefreshReason.TtlExpiry;

            try
            {
                var set = await loader(ct);

                _entries[key] = new Entry(set, clock.GetUtcNow(), generation);
                _isPopulated = true;
                Count(reason);

                return set;
            }
            catch (Exception ex) when (stale is not null && ex is not OperationCanceledException)
            {
                // Last-known-good, plus a negative cache. LoadedAt is deliberately NOT restamped —
                // that would pin a stale set for a full TTL after the database recovered — but the
                // failure IS stamped, so the next FailureBackoff seconds of callers short-circuit to
                // stale instead of each paying another connect timeout in turn.
                _entries[key] = stale with { LastFailureAt = clock.GetUtcNow() };
                Interlocked.Increment(ref _staleServedAfterFailure);
                logger.LogError(
                    ex,
                    "Capability refresh failed for org {OrgId}; serving the last known good set (version {Version}).",
                    orgId,
                    stale.Set.Version);

                return stale.Set;
            }
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <summary>
    /// Proves the seam is reachable and marks this replica ready. Returns false rather than throwing
    /// so the caller can retry — see <see cref="IsPopulated"/> for why readiness must not wait on
    /// inbound traffic.
    /// </summary>
    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var platform = scope.ServiceProvider.GetRequiredService<IPlatformScope>();
            var reader = scope.ServiceProvider.GetRequiredService<CapabilityStateReader>();

            await platform.RunAsync(() => reader.ProbeReachabilityAsync(ct), ct);

            _isPopulated = true;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The capability seam is not reachable yet; this replica stays not-ready.");
            return false;
        }
    }

    /// <summary>
    /// Marks every cached set stale. Called by the host's <c>CapabilityNotificationListener</c> on a
    /// <c>NOTIFY</c>, and deliberately lazy: it does not re-read anything, so a burst of notifications
    /// costs one interlocked increment each rather than a query storm across every cached org. The
    /// next <see cref="GetAsync"/> for a key pays for that key and nothing else.
    /// </summary>
    public void Invalidate() => Interlocked.Increment(ref _generation);

    /// <inheritdoc cref="Invalidate"/>
    public Task InvalidateAsync()
    {
        Invalidate();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ages every entry past the TTL without waiting 30 seconds. Test-only: it drives the TTL
    /// backstop path specifically, leaving the generation untouched so the resulting refresh is
    /// attributed to expiry and not to a notification. Any recorded failure is cleared too, so the
    /// negative cache cannot swallow the very refresh the test is asking for.
    /// </summary>
    internal void ForceExpireForTesting()
    {
        foreach (var (key, entry) in _entries)
        {
            _entries[key] = entry with { LoadedAt = DateTimeOffset.MinValue, LastFailureAt = null };
        }
    }

    /// <summary>Test-only: returns the cache to its cold-start state, including <see cref="IsPopulated"/>.</summary>
    internal void ResetForTesting()
    {
        _entries.Clear();
        _isPopulated = false;
    }

    private bool TryGetFresh(CacheKey key, out CapabilitySet set)
    {
        set = null!;

        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        var now = clock.GetUtcNow();

        if (entry.Generation == Interlocked.Read(ref _generation) && now - entry.LoadedAt < Ttl)
        {
            set = entry.Set;
            return true;
        }

        // Negative cache. The entry IS stale, but a refresh failed within the last FailureBackoff,
        // so retrying now would only buy another connect timeout — and, because refreshes serialize
        // per key, every caller behind this one would pay it in turn.
        if (entry.LastFailureAt is { } failedAt && now - failedAt < FailureBackoff)
        {
            set = entry.Set;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The out-of-band loader: one scope, one platform-scoped transaction, one read. Request-path
    /// refreshes supply their ambient loader instead — see the lifetime note on the class.
    /// </summary>
    private async Task<CapabilitySet> LoadAsync(Guid orgId, Guid? userId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<IPlatformScope>();
        var reader = scope.ServiceProvider.GetRequiredService<CapabilityStateReader>();

        return await platform.RunAsync(
            () => reader.ReadAsync(orgId, userId, requirePlatformScope: true, ct),
            ct);
    }

    private void Count(RefreshReason reason)
    {
        switch (reason)
        {
            case RefreshReason.ColdLoad:
                Interlocked.Increment(ref _coldLoads);
                break;
            case RefreshReason.Notification:
                Interlocked.Increment(ref _notificationRefreshes);
                break;
            default:
                Interlocked.Increment(ref _ttlRefreshes);
                break;
        }
    }

    private enum RefreshReason
    {
        ColdLoad,
        Notification,
        TtlExpiry,
    }

    /// <summary>Null <c>UserId</c> is the no-authenticated-user key (jobs, CLI, the nightly sweep).</summary>
    private readonly record struct CacheKey(Guid OrgId, Guid? UserId);

    /// <param name="LastFailureAt">
    /// When a refresh for this key last failed, or null. Drives the negative cache — see
    /// <see cref="FailureBackoff"/>. Cleared implicitly on every successful load, because that path
    /// constructs a fresh <see cref="Entry"/>.
    /// </param>
    private sealed record Entry(
        CapabilitySet Set,
        DateTimeOffset LoadedAt,
        long Generation,
        DateTimeOffset? LastFailureAt = null);
}
