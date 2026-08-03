using LeaseBook.Modules.Capabilities.Caching;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Wakes <see cref="CapabilityCache"/> when a capability row changes, by holding a long-lived
/// <c>LISTEN</c> on <see cref="Channel"/>.
/// <para>
/// <b>Host-owned, like the scheduler.</b> This resolves the host's <c>DbContext</c>, reads its
/// connection string and rewrites pooling, keepalive and application name — composition-root work
/// over the host's persistence driver, not module logic. <c>Npgsql</c> is the host's driver in the
/// same sense Hangfire is the host's scheduler, and no module references either. The module owns the
/// cache it wakes; the mechanics of holding a raw connection live here.
/// </para>
/// <para>
/// <b>It carries the wake-up signal ONLY, and cannot read the tables it is told about.</b> A
/// <c>LISTEN</c> connection holds no transaction, and <c>SET LOCAL</c> inside a transaction is the
/// only pooling-safe way to establish context — so this connection has neither <c>app.org_id</c> nor
/// <c>app.platform</c>, and every RLS'd platform table is invisible on it. The actual read runs later,
/// as a short platform-scoped transaction on an ordinary pooled connection (see
/// <c>CapabilityCache.LoadAsync</c>). Do not "optimize" by reading state here: it would return zero
/// rows rather than raising.
/// </para>
/// <para>
/// <b>The writer must issue NOTIFY inside its write transaction.</b> Postgres queues notifications and
/// delivers them after commit, preserving order, so a listener can never be woken before the row it
/// has to read is visible. Issued outside the transaction, that guarantee is gone and the race is
/// back. Task 12's CLI writer is bound by this.
/// </para>
/// <para>
/// <b>Keepalives are not optional.</b> Under ADR-027 private networking an idle TCP connection is
/// subject to load-balancer idle timeout. Without <c>Keepalive</c> the listener is silently dropped,
/// after which the deployment degrades permanently to the 30-second TTL — correct, but slower, and
/// with no symptom anywhere. <see cref="CapabilityCache.NotificationRefreshes"/> staying at zero is
/// the signal that this happened.
/// </para>
/// </summary>
public sealed class CapabilityNotificationListener(
    CapabilityCache cache,
    IServiceScopeFactory scopeFactory,
    ILogger<CapabilityNotificationListener> logger) : BackgroundService
{
    /// <summary>The channel name. Shared with the platform writer (Task 12) — do not fork it.</summary>
    public const string Channel = "leasebook_capabilities";

    /// <summary>Seconds. See the keepalive note on the class.</summary>
    private const int KeepaliveSeconds = 30;

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private long _notificationsReceived;
    private long _reconnects;
    private volatile bool _isListening;

    /// <summary>True while the <c>LISTEN</c> is established. The readiness probe (Task 7) may read it.</summary>
    public bool IsListening => _isListening;

    /// <summary>Notifications delivered on this connection since startup.</summary>
    public long NotificationsReceived => Interlocked.Read(ref _notificationsReceived);

    /// <summary>Times the listen connection was re-established after a failure.</summary>
    public long Reconnects => Interlocked.Read(ref _reconnects);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not hold up host startup: BackgroundService awaits ExecuteAsync up to its first yield.
        await Task.Yield();

        // Resolving the connection string is itself fallible — a malformed value makes
        // NpgsqlConnectionStringBuilder throw, and resolving the DbContext can fail outright. An
        // escape from ExecuteAsync would STOP THE HOST, because BackgroundServiceExceptionBehavior
        // defaults to StopHost and nothing here overrides it. That would take the API down over the
        // one component whose entire premise is that it is optional and the TTL is the correctness
        // floor. Degrade exactly as the missing-connection-string case does.
        string? connectionString;
        try
        {
            connectionString = BuildListenConnectionString();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "The capability listener could not resolve a connection string; capability changes " +
                "will propagate on the {Ttl}s TTL only.",
                CapabilityCache.Ttl.TotalSeconds);
            return;
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning(
                "No connection string is available for the capability listener; capability changes " +
                "will propagate on the {Ttl}s TTL only.",
                CapabilityCache.Ttl.TotalSeconds);
            return;
        }

        var backoff = InitialBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(connectionString, () => backoff = InitialBackoff, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _isListening = false;
                Interlocked.Increment(ref _reconnects);

                // Logged every time, at Warning: a flapping listener is a real degradation even
                // though the TTL keeps the system correct, and nothing else surfaces it.
                logger.LogWarning(
                    ex,
                    "Capability notification listener dropped (reconnect #{Reconnects}); retrying in {Backoff}.",
                    Reconnects,
                    backoff);

                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = backoff >= MaxBackoff
                    ? MaxBackoff
                    : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }

        _isListening = false;
    }

    private async Task ListenAsync(string connectionString, Action onConnected, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        connection.Notification += OnNotification;

        try
        {
            await connection.OpenAsync(ct);

            // The channel is an identifier, so it cannot be a parameter. It is a compile-time
            // constant, never user input.
            await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
            {
                await listen.ExecuteNonQueryAsync(ct);
            }

            _isListening = true;
            onConnected();
            logger.LogInformation("Capability notification listener subscribed to {Channel}.", Channel);

            while (!ct.IsCancellationRequested)
            {
                // Blocks until a notification arrives, raising Notification for each one.
                await connection.WaitAsync(ct);
            }
        }
        finally
        {
            _isListening = false;
            connection.Notification -= OnNotification;
        }
    }

    /// <summary>
    /// Runs on the connection's own callback path, so it must not block or touch the database.
    /// <see cref="CapabilityCache.Invalidate"/> is a single interlocked increment — the re-read happens
    /// later, on a pooled connection, in whichever request needs it next.
    /// </summary>
    private void OnNotification(object sender, NpgsqlNotificationEventArgs args)
    {
        Interlocked.Increment(ref _notificationsReceived);
        logger.LogDebug(
            "Capability notification received on {Channel} (payload '{Payload}').", args.Channel, args.Payload);

        cache.Invalidate();
    }

    /// <summary>
    /// Takes the host's configured connection and adds what a long-lived listen connection needs.
    /// Read from the registered <c>DbContext</c> rather than from <c>IConfiguration</c> so there is one
    /// source of truth — the test host rebinds the DbContext to a Testcontainers instance without
    /// touching <c>ConnectionStrings:Default</c>, and a config read would point this at the developer's
    /// local database instead.
    /// </summary>
    private string? BuildListenConnectionString()
    {
        using var scope = scopeFactory.CreateScope();
        var baseConnectionString = scope.ServiceProvider.GetRequiredService<DbContext>()
            .Database.GetConnectionString();

        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return null;
        }

        return new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            // Pooling off: this connection is held for the process's lifetime and carries session
            // state (the LISTEN registration). Returning it to the pool would hand that state to an
            // unrelated request.
            Pooling = false,
            KeepAlive = KeepaliveSeconds,
            TcpKeepAlive = true,
            ApplicationName = "leasebook-capability-listener",
        }.ConnectionString;
    }
}
