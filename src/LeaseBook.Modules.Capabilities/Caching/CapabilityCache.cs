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
/// <b>Lifetime.</b> Singleton — this is per-replica state. It therefore takes
/// <see cref="IServiceScopeFactory"/> and opens a scope per refresh rather than holding
/// <see cref="IPlatformScope"/> or a <c>DbContext</c>, either of which would be a captive scoped
/// dependency: validation would reject it, or worse, it would quietly reuse a disposed context.
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

    private readonly ConcurrentDictionary<CacheKey, Entry> _entries = new();

    // One refresh at a time across the whole cache: a NOTIFY invalidates every key at once, and a
    // stampede of concurrent requests would otherwise each open their own scope and transaction.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // Bumped by Invalidate(). An entry stamped with an older generation is stale regardless of age,
    // which is how a notification short-circuits the TTL without touching any timestamp.
    private long _generation;

    private long _coldLoads;
    private long _notificationRefreshes;
    private long _ttlRefreshes;
    private long _staleServedAfterFailure;

    /// <summary>False until the first successful load. The readiness probe gates on this.</summary>
    public bool IsPopulated { get; private set; }

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
    /// been invalidated.
    /// </summary>
    public async Task<CapabilitySet> GetAsync(Guid orgId, Guid? userId, CancellationToken ct = default)
    {
        var key = new CacheKey(orgId, userId);

        if (TryGetFresh(key, out var cached))
        {
            return cached;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Re-check: another caller may have refreshed this key while we queued.
            if (TryGetFresh(key, out cached))
            {
                return cached;
            }

            var generation = Interlocked.Read(ref _generation);
            _entries.TryGetValue(key, out var stale);

            var reason =
                stale is null ? RefreshReason.ColdLoad
                : stale.Generation != generation ? RefreshReason.Notification
                : RefreshReason.TtlExpiry;

            try
            {
                var set = await LoadAsync(orgId, userId, ct);

                _entries[key] = new Entry(set, clock.GetUtcNow(), generation);
                IsPopulated = true;
                Count(reason);

                return set;
            }
            catch (Exception ex) when (stale is not null && ex is not OperationCanceledException)
            {
                // Last-known-good. The entry keeps its old stamp, so the next call retries rather
                // than pinning a stale set for a full TTL after the database recovers.
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
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Marks every cached set stale. Called by <see cref="CapabilityNotificationListener"/> on a
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
    /// attributed to expiry and not to a notification.
    /// </summary>
    internal void ForceExpireForTesting()
    {
        foreach (var (key, entry) in _entries)
        {
            _entries[key] = entry with { LoadedAt = DateTimeOffset.MinValue };
        }
    }

    /// <summary>Test-only: returns the cache to its cold-start state, including <see cref="IsPopulated"/>.</summary>
    internal void ResetForTesting()
    {
        _entries.Clear();
        IsPopulated = false;
    }

    private bool TryGetFresh(CacheKey key, out CapabilitySet set)
    {
        if (_entries.TryGetValue(key, out var entry) &&
            entry.Generation == Interlocked.Read(ref _generation) &&
            clock.GetUtcNow() - entry.LoadedAt < Ttl)
        {
            set = entry.Set;
            return true;
        }

        set = null!;
        return false;
    }

    /// <summary>
    /// One scope, one platform-scoped transaction, one read. The scope is created per refresh —
    /// see the lifetime note on the class.
    /// </summary>
    private async Task<CapabilitySet> LoadAsync(Guid orgId, Guid? userId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<IPlatformScope>();
        var reader = scope.ServiceProvider.GetRequiredService<CapabilityStateReader>();

        CapabilitySet? set = null;
        await platform.RunAsync(
            async () => set = await reader.ReadAsync(orgId, userId, requirePlatformScope: true, ct),
            ct);

        return set ?? throw new InvalidOperationException(
            "The platform-scoped capability read completed without producing a set.");
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

    private sealed record Entry(CapabilitySet Set, DateTimeOffset LoadedAt, long Generation);
}
