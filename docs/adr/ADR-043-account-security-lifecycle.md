# ADR-043: Account security has an application and operator lifecycle

- **Status:** Accepted
- **Date:** 2026-09-06
- **Deciders:** Maintainers

## Context

Requiring an administrator to enroll MFA needs a reachable enrollment screen, a way to save recovery
codes, and an administrative recovery path. A real organization also needs its first administrator
before the migration toolkit can be used. Fixture seeders are not that provisioning path.

## Decision

The authenticated account-security page is reachable before required MFA enrollment. It presents a QR
code and manual setup key, confirms a TOTP code, and displays ten single-use recovery codes once.
The `qrcode` browser library encodes the QR image locally; no third-party rendering service receives
the setup secret. Secrets and codes stay in component memory, outside query caches and browser
storage. Password login still precedes recovery-code login.

Enrollment remains first-time setup. Reconfirmation cannot replace recovery codes. Password changes
require the current password. Enrollment confirmation, password changes, and MFA resets invalidate
older sessions: Identity security stamps are validated on every authenticated request. Refresh the
initiating session after successful self-service changes, including initial authenticator creation.
This adds an Identity read per authenticated request in exchange for prompt revocation.

First-admin provisioning and emergency MFA reset use the `accounts` CLI, running under operator-held
database access. There is no HTTP provisioning or reset endpoint. This supports a sole administrator
who cannot sign in, without granting an authenticated application session power to reset another
account. Operators verify identity independently before reset. The command requires the organization
id and email together, clears recovery codes, replaces the authenticator key, and invalidates sessions.
Initial passwords arrive through standard input rather than command arguments. Account mutations and
attributed, credential-free audit records commit in one organization-scoped transaction.

## Consequences

A real administrator can enroll, save recovery codes, and change a password in the application.
Emergency recovery and initial provisioning require an operator with database access; production
access and execution remain operator-gated. The existing org-context transaction and Identity stores
are reused, with no new tables. The command provisions an empty organization for import-first
onboarding, not fixture data. An operator must arrange secure initial credential delivery.

## Revisit trigger

Revisit when delegated user administration, invitations, or self-service authenticator replacement
is required. Those flows need their own authorization and identity-verification policy.

## Addendum — sessions have a ceiling, and signing out revokes everywhere (2026-09-21)

This ADR listed what invalidates older sessions — enrollment confirmation, password changes, MFA
resets — and each of those is an event that _happens to_ an account. Neither of the two ordinary ways
a session should end was on the list.

**A sign-in now expires twelve hours after it was issued, whatever the session does in between.**
`ExpireTimeSpan` is eight hours with `SlidingExpiration`, which bounds idleness and nothing else: a
tab that polls keeps renewing the ticket and the session never ends. The ceiling is measured from
sign-in and therefore cannot be read off the ticket's `IssuedUtc`, which the handler rewrites on each
renewal; it is stored in the ticket's `AuthenticationProperties.Items`, which renewals carry through
untouched. Twelve hours is one working day, so an operator signs in each morning and is not
interrupted during it.

That storage choice also fixes what `RefreshSignInAsync` does to the ceiling, which matters because
the flows above all call it. Identity re-signs the user with the _existing_ properties, so a password
change or an MFA enrollment reissues the cookie and inherits the same deadline. Only a real sign-in
mints a new one. A ceiling that any in-session action could push forward would not be one.

A ticket carrying no deadline is rejected rather than admitted. Such tickets exist only across the
deploy that introduced this; failing closed costs one re-authentication, and failing open would leave
an uncapped session that never acquires a cap.

**Signing out rotates the security stamp, which ends every session for that user, on every device.**
`SignOutAsync` deletes the browser's copy of the ticket and nothing else, and cookie tickets are
self-contained: nothing outside the ticket is consulted to decide it is still good. Deleting one copy
therefore says nothing about any other, which is why rotation rather than sign-out is what has to
carry this — and signing out is exactly the moment an operator is relying on it. The stamp is
per-user, so there is no narrower rotation available; revoking a single device would require a
server-side ticket store, which is not built. For a product holding trust money, reading "sign me out" as "everywhere" is the safer
resolution of that ambiguity, and it is a deliberate behavior change rather than a side effect: an
operator signed in on a phone and a laptop loses both.

`SessionLifetime` owns `OnValidatePrincipal` for both checks and calls
`SecurityStampValidator.ValidatePrincipalAsync` explicitly rather than chaining whatever delegate
`AddIdentity` left there. Assigning that event replaces the stamp validator, and a replacement that
forgot to call it would switch off the revocation this ADR already depends on while every request
still authenticated successfully — a silent regression no test outside `SessionLifetimeTests` would
see. The cookie handler is also bound to the application's `TimeProvider`, so its own expiry
arithmetic and the ceiling read one clock.

## Revisit trigger (addendum)

Revisit if per-device sign-out is wanted, or if operators report the twelve-hour ceiling interrupting
a working day. Both point at a server-side ticket store, which would make individual sessions
addressable and is the point at which the ceiling could be relaxed safely.
