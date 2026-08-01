# ADR-001: Background job scheduler — Hangfire on PostgreSQL

- **Status:** Accepted
- **Date:** 2026-06-12
- **Deciders:** Engineering

## Context

Phase 1 needs durable background work: statement generation/email, the nightly trust-equation and
statement tie-out sweep, and (Phase 2) Stripe webhook retries. The candidates are Hangfire and
Quartz.NET. Constraints: solo operator (visibility into what ran and what failed matters), no Redis
yet (see ADR-002), and the job runner must establish org context inside the job's transaction
before any data access (CLAUDE.md multi-tenancy: missing context fails closed).

## Decision

Use **Hangfire with `Hangfire.PostgreSql` storage**. Jobs are enqueued and persisted in the same
PostgreSQL instance as application data — no new infrastructure. The Hangfire dashboard gives the
solo operator first-class visibility into scheduled/processing/failed jobs. Hangfire binds to the
scheduler-agnostic `OrgScopedExecutor` (WP-05) so org context is set transactionally regardless of
scheduler choice.

No Hangfire code lands in M0 — this ADR records the decision only; first jobs arrive in M1+.

## Amendment (2026-07-31, M8 WP-11): first integration landed, dashboard reversed

The nightly trust-invariant sweep is the first job, and building it changed two things above.

**The dashboard is not mounted, in Phase 1 or beyond without a further decision.** The Decision
section cites operator visibility as a reason to prefer Hangfire; that rationale no longer holds as
written. The dashboard is an unauthenticated administrative surface over job state, and mounting it
for a single operator buys less than it costs. Visibility comes instead from the sweep's structured
log events (`LogEvents.InvariantViolation` / `InvariantSweepCompleted`, the ids Track B alerts on),
from the job's Failed state in Hangfire storage, and from the `leasebook_ops` read grant on the
`hangfire` schema. The rest of the Decision — Postgres storage, no new infrastructure, binding to
`OrgScopedExecutor` — stands as written and is what the sweep actually does.

**Hangfire's schema is owned by the runtime role**, not the migrator, and is pre-created by
`infra/db/bootstrap.sql`. This is not a stylistic choice: Hangfire installs and upgrades its own
objects, and its upgrade scripts issue `ALTER TABLE`, `ALTER SEQUENCE`, and `DROP INDEX` against
them, which Postgres permits only to the owner. Migrator ownership would pass on the day it shipped
and fail at app startup on the first version bump that carries a schema migration. The runtime role
therefore holds `CREATE` inside the `hangfire` schema — its only DDL privilege anywhere, and
deliberately scoped so it has none on the database or on `public`.

## Consequences

- One fewer moving part than a Redis-backed queue; storage is transactional with our data.
- Hangfire's polling model is fine at our scale; revisit if job volume grows.
- Quartz's richer scheduling (cron clustering) is not needed for our batch-shaped workloads.

## Revisit trigger

Job throughput or latency outgrows Postgres-backed polling, or we need multi-region/clustered
scheduling — re-evaluate Quartz or a managed queue at that point.
