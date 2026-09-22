using System.Net;
using System.Net.Http.Json;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration.Security;

/// <summary>
/// Two properties a sliding cookie session does not have on its own.
///
/// <para>
/// <b>An absolute cap.</b> <c>SlidingExpiration</c> renews the ticket on every request inside the
/// window, so an active tab keeps a session alive forever. The cap has to be measured from the
/// original sign-in, which means it cannot live in the ticket's own <c>IssuedUtc</c> — the handler
/// rewrites that on each renewal. The first test below slides the session deliberately, several
/// times, so a cap that a renewal could reset would pass every intermediate assertion and fail the
/// last one for the wrong reason: it is the sliding that makes the test meaningful.
/// </para>
///
/// <para>
/// <b>Revocation.</b> Cookie tickets are self-contained. <c>SignOutAsync</c> deletes the browser's
/// copy and nothing else, so a ticket captured before sign-out stays valid until it expires — the
/// exact case where revocation matters. Rotating the security stamp is what invalidates it, and
/// because <c>ValidationInterval</c> is <c>TimeSpan.Zero</c> the rejection lands on the very next
/// request. The second test holds a copied ticket precisely because a test driving one client
/// through sign-out cannot tell a revoked ticket from a deleted cookie.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SessionLifetimeTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";

    [Fact]
    public async Task Session_cannot_outlive_the_absolute_cap_however_often_it_slides()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new MutableClock(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));
        using var factory = WithClock(clock);
        var email = await NewAccountAsync(ct);

        var client = factory.CreateClient();
        await PrimeCsrfAsync(client, ct);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Each step is inside the 8h sliding window measured from the previous request, so the ticket
        // is renewed every time and its own expiry keeps moving forward. Only an absolute deadline
        // anchored at sign-in can end this.
        foreach (var elapsed in new[] { 4, 8, 11 })
        {
            clock.AdvanceTo(clock.Origin.AddHours(elapsed));
            var still = await client.GetAsync("/api/auth/me", ct);
            still.StatusCode.ShouldBe(HttpStatusCode.OK, $"the session should still be live at +{elapsed}h");
        }

        clock.AdvanceTo(clock.Origin.AddHours(12).AddMinutes(1));

        var after = await client.GetAsync("/api/auth/me", ct);
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Signing_out_revokes_a_ticket_that_was_copied_before_sign_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var email = await NewAccountAsync(ct);

        var client = fixture.Api.CreateClient();
        await PrimeCsrfAsync(client, ct);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The stolen copy: a second client carrying the same ticket, with no cookie jar of its own,
        // so nothing that happens to the first client's cookies can reach it.
        var ticket = ExtractCookie(login, "LeaseBook.Auth")
            ?? throw new InvalidOperationException("Login did not set the auth cookie.");
        var copy = fixture.Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        copy.DefaultRequestHeaders.Add("Cookie", $"LeaseBook.Auth={ticket}");

        (await copy.GetAsync("/api/auth/me", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Antiforgery tokens are bound to the authenticated identity, so the one primed while
        // anonymous stops matching the moment login succeeds.
        await PrimeCsrfAsync(client, ct);
        var logout = await client.PostAsync("/api/auth/logout", null, ct);
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent, await logout.Content.ReadAsStringAsync(ct));

        (await copy.GetAsync("/api/auth/me", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private WebApplicationFactory<Program> WithClock(TimeProvider clock) =>
        fixture.Api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(clock);
        }));

    private async Task<string> NewAccountAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Session Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"admin-{orgId:N}@example.com";
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            Id = UuidV7.NewId(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            OrgId = orgId,
            DisplayName = "Renée Calloway",
        };
        var created = await userManager.CreateAsync(user, Password);
        created.Succeeded.ShouldBeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));
        (await userManager.AddToRoleAsync(user, Roles.PMAdmin)).Succeeded.ShouldBeTrue();
        return email;
    }

    private static async Task PrimeCsrfAsync(HttpClient client, CancellationToken ct)
    {
        var response = await client.GetAsync("/api/auth/csrf", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var token = ExtractCookie(response, "XSRF-TOKEN")
            ?? throw new InvalidOperationException("CSRF endpoint did not set the XSRF-TOKEN cookie.");
        client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);
    }

    private static string? ExtractCookie(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            return null;
        }

        var prefix = name + "=";
        foreach (var cookie in setCookies)
        {
            if (cookie.StartsWith(prefix, StringComparison.Ordinal))
            {
                var value = cookie[prefix.Length..];
                var end = value.IndexOf(';');
                return Uri.UnescapeDataString(end >= 0 ? value[..end] : value);
            }
        }

        return null;
    }

    /// <summary>
    /// A clock the test moves. <see cref="Origin"/> is kept so each step is expressed as an offset
    /// from sign-in rather than from the previous step, which is what the cap is measured against.
    /// </summary>
    private sealed class MutableClock(DateTimeOffset origin) : TimeProvider
    {
        private DateTimeOffset _now = origin;

        public DateTimeOffset Origin { get; } = origin;

        public override DateTimeOffset GetUtcNow() => _now;

        public void AdvanceTo(DateTimeOffset now) => _now = now;
    }
}
