using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace LeaseBook.Web.Auth;

/// <summary>
/// Puts a ceiling on how long one sign-in can last, however active the session is.
///
/// <para>
/// <c>SlidingExpiration</c> renews the ticket whenever a request arrives inside the window, so
/// <see cref="CookieAuthenticationOptions.ExpireTimeSpan"/> bounds only idleness — an open tab
/// polling in the background keeps a session alive indefinitely. The ceiling therefore has to be
/// measured from the original sign-in, and it cannot be read off the ticket's own
/// <c>IssuedUtc</c>: the handler rewrites that on every renewal, so a deadline derived from it
/// would slide along with everything else. It lives in <see cref="AuthenticationProperties.Items"/>
/// instead, which is serialized into the ticket and carried through renewals untouched.
/// </para>
///
/// <para>
/// That also settles what <c>RefreshSignInAsync</c> does to it: Identity re-signs the user with the
/// <i>existing</i> properties, so a password change or an MFA enrollment reissues the cookie and
/// inherits the same deadline rather than starting a new twelve hours. Only a real sign-in mints a
/// new one, which is the intent — "absolute" that any in-session action can push forward is not
/// absolute.
/// </para>
///
/// <para>
/// A ticket with no deadline is rejected rather than admitted. Those exist only across the deploy
/// that introduced this, and the cost of failing closed is one re-authentication; the cost of
/// failing open is an uncapped session that never acquires a cap.
/// </para>
/// </summary>
public static class SessionLifetime
{
    /// <summary>
    /// The ceiling, measured from sign-in. One working day: an operator signs in each morning and is
    /// not interrupted during the day.
    /// </summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(12);

    /// <summary>
    /// Round-trip format, and parsed back with <see cref="DateTimeStyles.RoundtripKind"/>, so the
    /// value means the same instant regardless of the server's local zone at either end.
    /// </summary>
    private const string DeadlineKey = ".LeaseBook.SessionDeadline";

    /// <summary>
    /// Stamps the deadline on a ticket that has none. Wired to <c>OnSigningIn</c>.
    /// </summary>
    public static Task StampDeadlineAsync(CookieSigningInContext context)
    {
        if (!context.Properties.Items.ContainsKey(DeadlineKey))
        {
            var clock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
            context.Properties.Items[DeadlineKey] =
                (clock.GetUtcNow() + MaximumAge).ToString("O", CultureInfo.InvariantCulture);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Rejects an over-age ticket, then defers to Identity's security-stamp validation. Wired to
    /// <c>OnValidatePrincipal</c>.
    ///
    /// <para>
    /// The stamp call is made explicitly rather than by chaining whatever delegate
    /// <c>AddIdentity</c> left on the event. Assigning <c>OnValidatePrincipal</c> replaces it, and a
    /// replacement that forgot to call it would silently switch off revocation — the handler would
    /// still authenticate every request, so nothing fails and no test that is not specifically
    /// looking for revocation goes red. <c>SessionLifetimeTests</c> covers the pairing.
    /// </para>
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var clock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        if (!TryReadDeadline(context.Properties, out var deadline) || clock.GetUtcNow() >= deadline)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            return;
        }

        await SecurityStampValidator.ValidatePrincipalAsync(context);
    }

    private static bool TryReadDeadline(AuthenticationProperties properties, out DateTimeOffset deadline)
    {
        deadline = default;
        return properties.Items.TryGetValue(DeadlineKey, out var raw)
            && raw is not null
            && DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out deadline);
    }
}
