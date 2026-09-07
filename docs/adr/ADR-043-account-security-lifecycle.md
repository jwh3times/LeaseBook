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
