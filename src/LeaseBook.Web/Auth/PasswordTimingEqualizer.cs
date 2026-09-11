using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace LeaseBook.Web.Auth;

/// <summary>
/// Makes every sign-in attempt that ends in the generic 401 pay for one password-hash verification.
///
/// <para>
/// The uniform "Invalid credentials." answer exists so that a wrong password, a lockout and an unknown
/// email are indistinguishable — login must never reveal whether an email address has an account. The
/// body achieved that; the clock did not. Identity verifies a password hash only after it has found a
/// user and passed the pre-sign-in checks, so the arms that never reach the hasher returned in a small
/// fraction of the time the hashing arm took. The work is deliberately expensive (PBKDF2), which is
/// exactly what made its absence easy to see.
/// </para>
///
/// <para>
/// So the paths that skip the hasher call <see cref="SpendVerification"/> to do equivalent work and
/// discard the answer. The comparison is against a hash of a random password minted once at startup:
/// a real hash, produced by the configured <see cref="IPasswordHasher{TUser}"/>, so it costs whatever
/// a real verification costs and follows the hasher's configuration if it ever changes. Nothing can
/// authenticate against it, and it is never compared with a stored credential.
/// </para>
///
/// <para>
/// Accepted cost: this turns the cheap arms of an anonymous endpoint into tens of milliseconds of
/// CPU, so a rejected sign-in is now uniformly expensive. The per-IP rate limit on the auth policy
/// is what bounds that, and is load-bearing for this defense rather than only for brute force.
/// </para>
/// </summary>
public sealed class PasswordTimingEqualizer(IServiceScopeFactory scopeFactory)
{
    private readonly Lazy<string> _decoyHash = new(
        () =>
        {
            // Identity registers IPasswordHasher<T> as scoped, so it is resolved per use rather than
            // captured: this must cost whatever the *registered* hasher costs, including if one is
            // ever swapped in, and holding an instance from a disposed scope would quietly stop
            // being true. A scope costs microseconds against PBKDF2's tens of milliseconds.
            using var scope = scopeFactory.CreateScope();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();

            // Minted at first use, not a checked-in constant: a fixed hash would be a fixed target,
            // and hashing here means the cost tracks the hasher's real configuration.
            var decoyPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            return hasher.HashPassword(new AppUser(), decoyPassword);
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Does the hashing work at startup so the first sign-in of a process is not the one request with
    /// anomalous timing. Idempotent.
    /// </summary>
    public void Warm() => _ = _decoyHash.Value;

    /// <summary>
    /// Whether the decoy hash has been minted. Exists so a test can observe that the warm-up ran on
    /// the process modes that serve sign-ins and did not run on the ones that cannot (#367); it is
    /// not part of the equalizer's behaviour and nothing in the sign-in path reads it.
    /// </summary>
    internal bool IsWarm => _decoyHash.IsValueCreated;

    /// <summary>
    /// Performs one password verification against the decoy and discards the result. Call this on any
    /// sign-in path that reaches the generic 401 without the hasher having run.
    /// </summary>
    public void SpendVerification(string submittedPassword)
    {
        using var scope = scopeFactory.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();

        // A fresh instance per call rather than a shared field: the built-in hasher ignores this
        // argument, but the whole reason the hasher is resolved per call is that a different one
        // could be registered, and one that reads the user would make a shared instance a data race.
        //
        // The result is deliberately discarded — the point is the work, not the answer. It needs no
        // keep-alive: this is a virtual call through a DI-resolved interface, which the JIT cannot
        // prove pure and so cannot elide.
        _ = hasher.VerifyHashedPassword(new AppUser(), _decoyHash.Value, submittedPassword);
    }
}
