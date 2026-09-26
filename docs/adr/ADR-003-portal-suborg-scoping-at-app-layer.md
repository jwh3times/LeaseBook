# ADR-003: Portal sub-org scoping at the application layer, not in RLS

- **Status:** Accepted
- **Date:** 2026-06-12
- **Deciders:** Engineering

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
