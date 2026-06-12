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

## Consequences

- One fewer moving part than a Redis-backed queue; storage is transactional with our data.
- Hangfire's polling model is fine at our scale; revisit if job volume grows.
- Quartz's richer scheduling (cron clustering) is not needed for our batch-shaped workloads.

## Revisit trigger

Job throughput or latency outgrows Postgres-backed polling, or we need multi-region/clustered
scheduling — re-evaluate Quartz or a managed queue at that point.
