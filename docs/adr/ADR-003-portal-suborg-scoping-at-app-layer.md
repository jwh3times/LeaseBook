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
