# ADR-049: Portal invitations bind an explicit identity before granting access

- **Status:** Accepted
- **Date:** 2026-09-30
- **Deciders:** Jerry Holland
- **Amends:** [ADR-003](ADR-003-portal-suborg-scoping-at-app-layer.md) and
  [ADR-043](ADR-043-account-security-lifecycle.md) — portal provisioning gains a delegated invitation
  flow; administrator bootstrap and recovery retain their operator-only boundary.

## Context

The tenant and owner portals already resolve active access links on each request, with database
persona enforcement from ADR-048. Fixture provisioning cannot enroll real recipients. Invitation
acceptance starts before organization context exists and must establish exactly one intended
relationship without turning an email match into authority over financial records.

## Decision

PM admins and staff may manage portal invitations and revoke access. An admin-only organization
setting can restrict this to admins; each management mutation checks the current setting. The
manager explicitly selects a non-system tenant or owner and confirms a recipient address. Multiple
people may share that financial record, but each login retains one organization and one exclusive
portal persona.

An invitation expires after 72 hours. A purpose-bound Data Protection token identifies its
organization and durable invitation row; the token is authenticated before selecting organization
context. Acceptance rechecks state, expiry, target and account compatibility inside a fresh scoped
transaction through `OrgScopedExecutor`, as a named enrollment process. The fresh scope is necessary
because an already signed-in recipient has an ambient portal transaction that cannot grant access.
No portal policy gains organization-wide access. Account creation, role, access link, invitation
consumption and audit commit together. Acceptance, replacement, cancellation and revocation must
serialize so a revoked invitation cannot restore access through a race.

New recipients choose their own password and finish at normal sign-in. Existing accounts sign in
normally, including MFA, before accepting; acceptance preserves their credentials and security
settings. Their organization, email and exclusive persona must match, and a revoked account may be
invited back only to the same historical target. Rebinding to another target, combining personas and
multi-organization membership remain separate decisions.

The first delivery adapter writes only to a local development test inbox, outside public/static
paths. Delivery follows the invitation transaction's commit and has a separately visible outcome;
it is never presented as real email delivery. Redeemable tokens are generated for delivery and are
not stored in invitation rows or audit snapshots. The browser receives proof in a URL fragment,
removes it from the address bar, and keeps it only in component memory through enrollment and
ordinary sign-in. Tokens never become query-cache keys, persisted browser state or telemetry.

The local inbox uses a one-second `BackgroundService` poll of committed invitation rows, enabled
only in the Development web process and independent of `Jobs:Enabled`. This is a bounded exception
to ADR-001's Hangfire scheduler default, like ADR-046's isolated simulation worker: local enrollment
must run with production scheduled jobs disabled. The durable invitation row owns pending delivery;
restarting the worker does not lose queued work. This choice does not select a production delivery
scheduler.

## Consequences

Enrollment can be exercised end to end locally without a live email provider. Mailbox possession
does not independently establish the Directory relationship: the inviting manager remains
responsible for selecting the right person and record. Production delivery and rollout remain
deferred until a provider and deployment acceptance are in place. Administrator account creation,
credential recovery, email changes and financial actions are outside this flow.

## Revisit trigger

Revisit for a real email provider, self-service recovery, a login needing more than one organization
or persona, or a request to transfer an existing login to another financial record.
