# Architecture Decision Records

An **Architecture Decision Record** captures a single significant or non-obvious engineering
decision — the context that forced it, the choice made, and the consequences accepted — so it can be
understood later without archaeology through chat logs or commit history. LeaseBook keeps them as
lightweight [Michael Nygard–format](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)
records, one file per decision, numbered sequentially.

The rule (see [ADR-000](ADR-000-record-architecture-decisions.md)): **every deviation from a
build-plan default gets an ADR**, and every ADR carries an explicit *revisit trigger* — the
observable condition that should reopen it. ADRs hold engineering rationale only; pricing, strategy,
and customer detail stay out of the public repo.

To add one: copy [`template.md`](template.md), give it the next number in `ADR-NNN-kebab-title.md`
form, write it, and add a row to the index below. When a decision is replaced, mark the old record
**Superseded by ADR-XXX** rather than deleting it — the history is the point.

## Index

| ADR | Title | Status | Date |
| --- | --- | --- | --- |
| [000](ADR-000-record-architecture-decisions.md) | Record architecture decisions | Accepted | 2026-06-12 |
| [001](ADR-001-background-job-scheduler.md) | Background job scheduler — Hangfire on PostgreSQL | Accepted | 2026-06-12 |
| [002](ADR-002-defer-redis.md) | Defer Redis | Accepted | 2026-06-12 |
| [003](ADR-003-portal-suborg-scoping-at-app-layer.md) | Portal sub-org scoping at the application layer, not in RLS | Accepted | 2026-06-12 |
| [004](ADR-004-single-appdbcontext-in-host.md) | One AppDbContext, owned by the host | Accepted | 2026-06-12 |
| [005](ADR-005-cqrs-owned-dispatcher-no-mediatr.md) | CQRS via an owned dispatcher + FluentValidation; no MediatR, no AutoMapper | Accepted | 2026-06-12 |
| [006](ADR-006-posting-template-catalog.md) | Posting-template catalog & dual-basis journal | Accepted | 2026-06-12 |
| [007](ADR-007-cross-module-read-contracts.md) | Cross-module reads go through consumer-owned ports, not shared SQL | Accepted | 2026-06-12 |
| [008](ADR-008-journal-dimension-fks-and-aggregates.md) | Journal-dimension FKs and system aggregate rows | Accepted | 2026-06-13 |
| [009](ADR-009-search-and-pagination.md) | Search via pg_trgm + GIN; consistent paged list contract | Accepted | 2026-06-13 |
| [010](ADR-010-ledger-write-command-surface-and-actor-attribution.md) | Ledger write commands wrap the engine; the actor is attributed at the seam | Accepted | 2026-06-13 |
| [011](ADR-011-deposit-prepayment-over-application-policy.md) | An application may not exceed the open receivable (warn + block) | Accepted | 2026-06-13 |
| [012](ADR-012-openapi-client-drift-gate.md) | Enforce the generated API client with a build-time OpenAPI drift gate | Accepted | 2026-06-15 |
| [013](ADR-013-composite-org-dimension-fks.md) | Promote the journal-dimension FKs to composite `(org_id, id)` | Accepted | 2026-06-20 |
| [014](ADR-014-reconciliation-engine-and-lock.md) | Bank reconciliation — engine placement, clearance model, and the hybrid lock | Accepted | 2026-06-20 |
| [015](ADR-015-csv-statement-import-and-matching.md) | CSV bank-statement import, auto-match, and dedup | Accepted | 2026-06-21 |
| [016](ADR-016-reporting-read-layer.md) | Reporting read layer — Accounting owns the statement engine | Accepted | 2026-06-22 |
| [017](ADR-017-rent-proration.md) | Rent proration method | Accepted | 2026-06-23 |
| [018](ADR-018-management-fee-rounding.md) | Management-fee rounding for the owner disbursement run | Accepted | 2026-06-23 |
| [019](ADR-019-bulk-run-engine-and-batch-posting.md) | Bulk run engine and batch posting | Accepted | 2026-06-23 |
| [020](ADR-020-opening-balance-posting.md) | Balance-forward opening-balance posting model | Accepted | 2026-06-23 |
| [021](ADR-021-migration-toolkit.md) | Migration toolkit architecture & verification gate | Accepted | 2026-06-23 |
| [022](ADR-022-e2e-in-ci-and-a11y-gate.md) | e2e in CI + automated accessibility gate | Accepted | 2026-06-30 |

## Status legend

- **Proposed** — under discussion, not yet in effect.
- **Accepted** — in effect; the codebase should reflect it.
- **Superseded by ADR-XXX** — replaced; kept for the historical record.
