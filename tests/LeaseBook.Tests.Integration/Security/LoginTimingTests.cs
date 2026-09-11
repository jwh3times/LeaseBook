using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Shouldly;

namespace LeaseBook.Tests.Integration.Security;

/// <summary>
/// Sign-in answers a wrong password, a locked-out account and an unknown email with one code and one
/// message, so that nothing reveals whether an email address has an account.
/// <c>AuthEndpointsTests.A_rejected_credential_never_reveals_which_credential_it_was</c> pins that for
/// the response body. This pins it for the clock, which the body assertion cannot see: the expensive
/// step of a sign-in is the password-hash verification, and an arm that never reaches the hasher
/// returns in a small fraction of the time — the same distinction, on a different channel.
///
/// <para>
/// Asserted as a <b>ratio</b> rather than a duration. A slow or contended CI machine scales every arm
/// together, so the ratio is stable where a millisecond threshold would not be, and the test needs no
/// knowledge of how fast the hasher is on any particular box.
/// </para>
///
/// <para>
/// Medians, not means, because a single scheduler hiccup moves a mean and does not move a median. The
/// bound is deliberately loose: the difference this exists to catch is roughly an order of magnitude,
/// so a bound near the measured margin would buy no detection and cost flakes.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class LoginTimingTests(PostgresFixture fixture)
{
    private const int Samples = 12;

    /// <summary>
    /// Bounds on the ratio between two arms that must be indistinguishable.
    ///
    /// <para>
    /// Two-sided on purpose. Under-payment is the obvious failure: an arm that skips the hasher runs
    /// at a few percent of one that does not. Over-payment is the failure this suite missed the first
    /// time — charging an arm twice is just as good a signal as charging it not at all, and a
    /// one-sided floor cannot see it.
    /// </para>
    ///
    /// <para>
    /// Loose by design: the differences worth catching are roughly a factor of two or more, so a
    /// bound near the measured margin would buy no detection and cost flakes. Measured ~0.93 with the
    /// equalizer, ~0.04 without it, and ~2.0 with the lock-tripping attempt double-charged.
    /// </para>
    /// </summary>
    private const double MinimumRatio = 0.5;

    private const double MaximumRatio = 1.6;

    [Fact]
    public async Task A_rejected_sign_in_costs_the_same_whether_or_not_the_account_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "Login Timing Org", ct);

        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);

        // A fresh account per sample. Reusing one trips the lockout after five failures, and a
        // locked-out account short-circuits ahead of the hasher — so the arm that is supposed to pay
        // for hashing silently stops paying, and the test passes for the wrong reason.
        var accounts = new List<string>();
        for (var i = 0; i < Samples + 3; i++)
        {
            var email = $"timing-{UuidV7.NewId():N}@example.com";
            await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Timing", Roles.PMStaff, ct);
            accounts.Add(email);
        }

        async Task<double> AttemptAsync(string email)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await client.PostAsJsonAsync(
                "/api/auth/login", new LoginRequest(email, "not-the-password"), ct);
            // Without this a tripped rate limiter turns every arm into an equally fast 429 and the
            // ratio below passes having measured nothing.
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        // Warm JIT, the connection pool and the hasher so the first sample is not an outlier.
        for (var i = 0; i < 3; i++)
        {
            await AttemptAsync(accounts[Samples + i]);
            await AttemptAsync($"absent-{UuidV7.NewId():N}@example.com");
        }

        var existing = new List<double>();
        var absent = new List<double>();
        for (var i = 0; i < Samples; i++)
        {
            // Interleaved, so drift and contention land on both arms equally.
            existing.Add(await AttemptAsync(accounts[i]));
            absent.Add(await AttemptAsync($"absent-{UuidV7.NewId():N}@example.com"));
        }

        var existingMedian = Median(existing);
        var absentMedian = Median(absent);

        // Guards the guard: if the hashing arm were itself near-instant, the ratio below would be
        // satisfied by two equally fast paths and prove nothing about the work being done.
        existingMedian.ShouldBeGreaterThan(
            5, "a rejected sign-in against a real account should be paying for a password hash");

        var ratio = absentMedian / existingMedian;
        ratio.ShouldBeGreaterThan(
            MinimumRatio,
            $"an unknown email answered in {absentMedian:F1}ms against {existingMedian:F1}ms for a "
            + "real account: the cheap path is back, and response time tells an attacker which "
            + "addresses have accounts even though the response body does not");
        ratio.ShouldBeLessThan(
            MaximumRatio,
            $"an unknown email answered in {absentMedian:F1}ms against {existingMedian:F1}ms for a "
            + "real account: it is now the slow arm, which separates the two just as well");
    }

    /// <summary>
    /// Three arms across the lockout boundary, because the boundary is where this went wrong.
    /// `LockedOut` comes back from two different places: from the pre-sign-in check, ahead of the
    /// hasher, and again from the attempt that trips the counter — which has already paid for a hash.
    /// A first version of this fix treated both as unpaid and charged the tripping attempt twice, so
    /// it answered in about double the time and marked the exact moment an account locked. Sampling
    /// only the already-locked attempt walked straight past it.
    /// </summary>
    [Fact]
    public async Task Every_attempt_across_the_lockout_boundary_costs_the_same()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        await AuthTestSupport.CreateOrgAsync(fixture, orgId, "Lockout Timing Org", ct);

        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);

        async Task<double> AttemptAsync(string email)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await client.PostAsJsonAsync(
                "/api/auth/login", new LoginRequest(email, "not-the-password"), ct);
            // Without this a tripped rate limiter turns every arm into an equally fast 429 and the
            // ratios below pass having measured nothing.
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        const int Rounds = 6;
        var beforeLock = new List<double>();   // attempt 1: a plain password rejection
        var trippingLock = new List<double>(); // attempt 5: trips MaxFailedAccessAttempts = 5
        var afterLock = new List<double>();    // attempt 6: already locked
        for (var i = 0; i < Rounds; i++)
        {
            var email = $"lockout-{UuidV7.NewId():N}@example.com";
            await AuthTestSupport.CreateUserAsync(fixture, orgId, email, "Lockout", Roles.PMStaff, ct);

            beforeLock.Add(await AttemptAsync(email));
            for (var attempt = 2; attempt <= 4; attempt++)
            {
                await AttemptAsync(email);
            }

            trippingLock.Add(await AttemptAsync(email));
            afterLock.Add(await AttemptAsync(email));
        }

        var baseline = Median(beforeLock);
        baseline.ShouldBeGreaterThan(
            5, "a rejected sign-in against a real account should be paying for a password hash");

        ShouldMatch(Median(trippingLock), baseline, "the attempt that trips the lockout");
        ShouldMatch(Median(afterLock), baseline, "an attempt against an already-locked account");
    }

    private static void ShouldMatch(double arm, double baseline, string what)
    {
        var ratio = arm / baseline;
        ratio.ShouldBeGreaterThan(
            MinimumRatio,
            $"{what} answered in {arm:F1}ms against {baseline:F1}ms for a plain password rejection: "
            + "it is skipping work the other arm does, and the difference says an account exists");
        ratio.ShouldBeLessThan(
            MaximumRatio,
            $"{what} answered in {arm:F1}ms against {baseline:F1}ms for a plain password rejection: "
            + "it is doing work the other arm does not, which separates the two just as well");
    }

    private static double Median(List<double> samples)
    {
        var sorted = samples.Order().ToList();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
