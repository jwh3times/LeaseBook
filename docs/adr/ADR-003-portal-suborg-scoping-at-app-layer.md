# ADR-003: Portal sub-org scoping at the application layer, not in RLS

- **Status:** Accepted
- **Date:** 2026-06-12
- **Deciders:** Engineering
- **Amended by:** [ADR-048](ADR-048-per-persona-row-level-security.md) — the revisit trigger fired
  at seven portal endpoints: row-level security now also enforces the portal personas inside the
  organization, and the application-layer checks below remain as the first layer.

## Context

Postgres Row-Level Security is the tenant-isolation boundary, scoped to the **org** via
`app.org_id` (CLAUDE.md). Phase 2-3 introduce owner and tenant portal users who need _sub-org_
visibility: an owner sees only their own properties; a tenant only their own ledger. One option is
to push these personas into RLS by stacking more session variables and per-persona policies. With
four personas across many tables, that multiplies policy complexity for little gain while the
portal surface is small.

## Decision

RLS stays **org-level only**. Sub-org visibility for portal personas is enforced at the application
layer: dedicated portal endpoints plus authorization handlers that constrain queries to the
caller's owned entities. The tenant-isolation test pack is extended in Phase 2 with portal-persona
cases (owner A cannot fetch owner B's data within the same org).

## Consequences

- RLS policies remain simple, bare-equality, index-aligned predicates.
- Portal authorization correctness becomes application-test responsibility, not a database
  guarantee — so those tests are mandatory, not optional.

## Revisit trigger

If portal endpoints grow past a handful, or a portal-scoping defect ships, reconsider per-persona
RLS policies (e.g., `app.owner_id` / `app.tenant_id` session variables with dedicated policies).

## Tenant own-ledger implementation (2026-09-26)

The first tenant slice binds an Identity user to a non-system Directory tenant through the
host-owned `resident_access` table. Each user has at most one active link; multiple users can share
one tenant financial account. Revoked links remain as history. Composite `(org_id, id)` references
enforce organization consistency for both the Identity user and Directory tenant, and the link
table uses the normal forced organization RLS policy. Identity stays RLS-exempt; provisioning and
resolution explicitly check the user's organization. Provisioning and revocation use the attributed
audit mechanism and expose no HTTP write surface.

`GET /api/portal/tenant/ledger` takes no tenant or organization selector. The early authorization
policy admits only the Tenant persona, excluding staff and Owner roles. An endpoint filter then
calls a scoped authorization handler inside the existing organization transaction, before dispatch
to Accounting. The handler resolves the active link and non-system Directory identity afresh on
every request; missing, revoked, invalid, or system links deny access even with an existing cookie.
The resident identity is never cached across requests or carried in the cookie.

The host projects the existing Accounting rent-ledger query into a resident response: display name,
dated categories, charges, payments, running balance, and void/reversal flags. It exposes no staff
descriptions, internal identifiers, owner data, source references, or bank details. Responses are
marked `no-store`. The rent ledger balance excludes security deposits and is not an online-payment
quote. The SPA routes Tenant sign-in to `/portal/tenant`, rejects staff navigation before mounting
staff data hooks, and reuses account security and sign-out. Owner access, payments, and production
enrollment remain outside this slice. The non-production `portal` fixture is a demonstration only.

## Owner read-only portal (2026-09-29)

The owner slice mirrors the tenant one. The host-owned `owner_access` table binds an Identity user to
a non-system Directory owner, with the same composite organization references, forced organization
RLS policy, one active link per user, revoked-link history and audited provision/revoke without an
HTTP write surface. The Owner persona is exclusive: its policy and scoped handler refuse a principal
that also holds PMAdmin, PMStaff or Tenant, and the tenant persona continues to refuse Owner. The link
and the non-system owner are resolved afresh on every request inside the organization transaction.

`/api/portal/owner` takes no owner, property or organization selector. The summary presents owner
equity on the organization's accounting basis, labelled with that basis. Deposit liabilities are
excluded because they are tenants' money, not the owner's. Disbursement history derives from the
owner's `OwnerDisbursed` ledger rows, and activity rows carry an allow-listed category and the
address of a property present on the owner's own rows, so a transferred-away property keeps its
history. The response exposes no descriptions, entry ids, source references, bank details, tenant
identities or PM-income figures.

Statements are never assembled live for an owner: live assembly carries staff line descriptions and
the organization-wide bank reconciliation. The portal lists every issued statement artifact for the
owner with its issue date, without inventing current or superseded labels (ADR-045 leaves amendment
semantics to compliance review), and serves the stored PDF byte-for-byte. That PDF is the document
the manager issued to this owner, so it carries whatever issued statements carry, including line
descriptions; the portal's own no-leak guarantee covers its JSON responses, not that document. A
foreign, cross-organization or nonexistent artifact id yields the same not-found; an owned artifact
whose bytes are missing from the artifact store yields a distinct, logged "document unavailable"
result, since the local store is not durable.

With this slice, the persona-scoped portal surface is seven endpoints: four tenant and three owner.
The revisit trigger above is evaluated against that count. Owner enrollment, durable artifact
storage and owner-initiated actions remain outside this slice; the non-production `portal` fixture
adds owner logins for demonstration only.
