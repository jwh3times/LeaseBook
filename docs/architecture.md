# Architecture

This is the conceptual companion to the README's [Architecture](../README.md#architecture) section:
it explains how the pieces fit together and links the decisions that shaped them. The binding rules
live in [`CLAUDE.md`](../CLAUDE.md) and the [ADRs](adr/); this document orients you, it does not
restate them.

LeaseBook is a **modular monolith** whose core is a double-entry trust-accounting engine. Every
tenant ledger, owner ledger, bank register, and statement is a *projection of one journal* — never a
separately maintained number that could drift. Correctness is structural, not a reporting concern,
and the architecture exists to keep it that way.

## System overview

One ASP.NET Core host (`LeaseBook.Web`) composes a set of module projects and serves both a JSON API
under `/api` and the built React SPA. The SPA (`web/`) talks to the host through a typed client
generated from the host's OpenAPI document. PostgreSQL is the single datastore. In production the
same container image serves the SPA and the API on one port, deployed to Azure Container Apps via the
Bicep in [`infra/`](../infra); locally, a Docker Compose `full` profile runs the equivalent stack.
See the README [port map](../README.md#port-map) for every port the project binds.

## Modules and boundaries

Each bounded context is its own project — `Accounting`, `Directory`, `Banking`, `Reporting`,
`Operations`, `Payments`, `Migrator` — over a shared `SharedKernel` that holds only cross-cutting
primitives (money, ids, the CQRS spine, tenancy, result types). A module references `SharedKernel`
and nothing else; the architecture tests (`ModuleBoundaryTests`) enforce this absolutely.

A module **never reads another module's tables or types directly**. A cross-module read goes through
a **consumer-owned port** — an interface declared in the *consuming* module's `Contracts` — that a
thin **host adapter** implements by delegating to the producing module via `ISender`, on the same
ambient row-level-security transaction. Ports expose **batch** reads (they return a map), never
per-id reads. This keeps every module independently extractable and keeps the boundary visible. The
one sanctioned exception is a dedicated reporting/read layer, which may read across the schema on
purpose and records its own ADR. See [ADR-007](adr/ADR-007-cross-module-read-contracts.md).

## The accounting core

The journal is `journal_entries` + `journal_lines`, written **only** through posting templates keyed
to business events (`RentCharged`, `PaymentReceived`, `DepositApplied`, …). Every line is tagged
`cash`, `accrual`, or `both`, so each accounting basis is a *query*, not a transformation — the two
bases are two readings of the same history and can never disagree about the past. Account *class*
(not a report filter) keeps fiduciary money separated: management income can never carry an owner's
name, and deposits/prepayments are liabilities until applied. This module carries the highest test
rigor in the codebase — invariant, property-based, and golden-file suites. See
[`accounting.md`](accounting.md) for the plain-English model and
[ADR-006](adr/ADR-006-posting-template-catalog.md) /
[ADR-008](adr/ADR-008-journal-dimension-fks-and-aggregates.md) for the engine decisions.

## Request flow (CQRS pipeline)

The application pattern is **CQRS with vertical slices**. An endpoint binds the request, dispatches a
command or query record through a hand-rolled `ISender`, and maps the result — nothing more. The
dispatcher runs a decorator pipeline in pinned order (telemetry outermost, then validation, then the
handler). Each slice has one colocated FluentValidation validator — the single validation home.
Commands mutate only through domain services; queries read projections or SQL within their own
module. Endpoints are minimal APIs only (`TypedResults`), no MVC controllers; there is no MediatR and
no AutoMapper. See [ADR-005](adr/ADR-005-cqrs-owned-dispatcher-no-mediatr.md).

## Multi-tenancy and security

PostgreSQL **row-level security is the tenant-isolation boundary** — EF Core global query filters are
ergonomics layered on top, not the boundary. Org context is set per-transaction with
`SET LOCAL app.org_id` (never session-level, which would leak across pooled connections); missing
context fails closed. Three database roles separate concerns: `leasebook_migrator` (schema owner),
`leasebook_app` (runtime, `FORCE ROW LEVEL SECURITY`), and `leasebook_ops` (read-only). Every
org-scoped table is created through the migrations RLS helper (column + `USING`/`WITH CHECK` policy +
`FORCE` in one call), and a schema-guard test fails CI if any `org_id` table lacks its policy. Portal
sub-org visibility (an owner sees only their properties) is enforced at the application layer rather
than by stacking more RLS policies — see [ADR-003](adr/ADR-003-portal-suborg-scoping-at-app-layer.md).
The security model and reporting process are in [SECURITY.md](../SECURITY.md).

## Frontend and the generated API client

The SPA is React 19 + TypeScript on Vite, with TanStack Query for server state. Reusable UI
primitives live in the design system (`web/src/design`); app-level shared components composed above
them (page scaffolds, modals, the record quick-switch) live in `web/src/components`; and
`web/src/lib` holds pure TypeScript utilities and hooks only. Money always renders through the
`<Money>` primitive with
tabular numerals and the organization's negative-display preference, and status is never conveyed by
color alone. The typed API client (`web/src/api/schema.d.ts`) is **generated** from the host's
OpenAPI document, never hand-edited. A build-time drift gate regenerates the client from a build-time
copy of the contract and fails CI if the committed file is stale, so the frontend and backend
contracts cannot silently diverge — see [ADR-012](adr/ADR-012-openapi-client-drift-gate.md).

## Data and persistence

A **single `AppDbContext`**, owned by the host, discovers each module's `IEntityTypeConfiguration`
implementations by assembly scan; modules contribute mappings but do not each carry their own
context (one database, one transaction per request — the RLS boundary). See
[ADR-004](adr/ADR-004-single-appdbcontext-in-host.md). Migrations are authored in the host and
applied by the `leasebook_migrator` role through a one-shot migrator image — **never at app
startup**. Money is `decimal` in C# and `NUMERIC(14,2)` in Postgres, end to end, never floating
point. The journal and audit tables are append-only: the runtime role holds no `UPDATE`/`DELETE`
grant on them, so corrections can only ever be linked reversals.

## Background work

Durable background jobs (statement generation/email, the nightly trust-equation sweep, future
webhook retries) are designed to run on **Hangfire backed by PostgreSQL** — no extra infrastructure.
Hangfire is **not yet integrated**: the first real integration is the nightly invariant sweep
(ROADMAP WP-11), and the Hangfire dashboard is deliberately not mounted in Phase 1 (attack surface).
Redis is deliberately deferred until a concrete need appears. Every job must establish org context
transactionally before touching data and throw if it is missing. See
[ADR-001](adr/ADR-001-background-job-scheduler.md) and [ADR-002](adr/ADR-002-defer-redis.md).

## Deployment

The production image serves the SPA and `/api` on the container's port `8080` and runs on Azure
Container Apps (East US 2), with secrets in Key Vault accessed by managed identity. Infrastructure is
declared as Bicep modules in [`infra/`](../infra) (see [`infra/README.md`](../infra/README.md));
migrations run as a separate one-shot job before the app rolls. The full stack runs locally through
Docker Compose — `./scripts/dev.ps1 app-up` brings up database → migrate → seed → app.

## Related documents

- [README](../README.md) — overview, getting started, and the port map
- [`blueprint.md`](blueprint.md) — the committed architecture blueprint (tech defaults, RLS design,
  trust-accounting data model)
- [`accounting.md`](accounting.md) — the trust-accounting model in plain English
- [`planning/`](planning/) — published milestone retrospectives (point-in-time)
- [`adr/`](adr/) — architecture decision records (start with the [index](adr/README.md))
- [`runbooks/`](runbooks/) — local development and restore runbooks
- [`CLAUDE.md`](../CLAUDE.md) — the engineering constraints and non-negotiable invariants
