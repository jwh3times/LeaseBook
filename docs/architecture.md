# Architecture

- **Audience:** Contributors and maintainers
- **Status:** Living architecture guide
- **Owner:** Maintainers
- **Last reviewed:** 2026-09-02

This is the canonical public map of the system **as implemented**. It explains how the pieces fit
together and links the decisions that shaped them without reproducing every invariant. Accepted
[ADRs](adr/README.md) own decision rationale, and the code and architecture tests are the executable
truth. Cross-agent working rules live in [`AGENTS.md`](../AGENTS.md).

LeaseBook is a **modular monolith** whose core is a double-entry trust-accounting engine. Every
tenant ledger, owner ledger, bank register, and statement is a _projection of one journal_ — never a
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
`Operations`, `Capabilities`, `Payments`, `Migrator` — over a shared `SharedKernel` that holds only
cross-cutting primitives (money, ids, the CQRS spine, tenancy, result types). All of them carry real
behavior except `Payments`, which remains a scaffolded shell for the online-payments phase. A module
references `SharedKernel`
and nothing else; the architecture tests (`ModuleBoundaryTests`) enforce this absolutely.

A module **never reads another module's tables or types directly**. A cross-module read goes through
a **consumer-owned port** — an interface declared in the _consuming_ module's `Contracts` — that a
thin **host adapter** implements by delegating to the producing module via `ISender`, on the same
ambient row-level-security transaction. Ports expose **batch** reads (they return a map), never
per-id reads. This keeps every module independently extractable and keeps the boundary visible. The
one sanctioned exception is a dedicated reporting/read layer, which may read across the schema on
purpose and records its own ADR. See [ADR-007](adr/ADR-007-cross-module-read-contracts.md).

Cross-module writes use the same visible composition seam: the consuming module owns a narrow port,
and the host adapter dispatches the producing module's command inside the ambient transaction. A
property ownership transfer uses this path from Directory to Accounting so its append-only ownership
transition, current Directory owner and deposit-responsibility handoff commit or roll back together. See
[ADR-036](adr/ADR-036-effective-dated-property-ownership-transfer.md).

Directory does not persist snapshots of facts already owned by those sources. Tenant financial
standing is a batch projection from Accounting's journal-derived aging and held-prepayment reads, and
unit occupancy is derived from the lease effective on the requested date. Tenant rows retain only
operational lifecycle; unit rows retain only operational availability. This lets delinquency coexist
with credit on file and lets an occupied unit be unavailable without either source contradicting the
other. See [ADR-038](adr/ADR-038-derived-financial-standing-and-occupancy.md).

## The accounting core

The journal is `journal_entries` + `journal_lines`, written **only** through posting templates keyed
to business events (`RentCharged`, `PaymentReceived`, `DepositApplied`, …). Every line is tagged
`cash`, `accrual`, or `both`, so each accounting basis is a _query_, not a transformation — the two
bases are two readings of the same history and can never disagree about the past. Account _class_
(not a report filter) keeps fiduciary money separated: management income can never carry an owner's
name, and deposits/prepayments are liabilities until applied. This module carries the highest test
rigor in the codebase — invariant, property-based, and golden-file suites. See
[`accounting.md`](accounting.md) for the plain-English model and
[ADR-006](adr/ADR-006-posting-template-catalog.md) /
[ADR-008](adr/ADR-008-journal-dimension-fks-and-aggregates.md) for the engine decisions.

## The capability seam

Whether a behavior is available is answered by one seam (`ICapabilityGate`) over two independent
sources: **feature flags**, which are deployment-wide operations toggles, and **entitlements**, which
are per-organization grants, with per-organization/per-user **cohorts** for betas. Entitlement gates
first, so a rollout can never hand out a paid capability, and an explicit flag kill beats a cohort, so
an emergency shutoff is not silently a customer downgrade. The catalog of what exists is **source
code**, never rows — the database stores state only.

Capabilities gate **reachability**, never money: a capability may decide whether a posting path runs
at all, but never what an accounting event produces. Money-affecting parameters live in `OrgSettings`.
Architecture tests keep the seam out of `Accounting` and out of `SharedKernel`, and a bulk run freezes
its capability set once at run-confirmation entry so one run cannot straddle a toggle. Cheap reads are served
from a short-lived in-process cache invalidated by a Postgres `NOTIFY`; money-path reads bypass it and
resolve inside the ambient transaction. The platform tables use an RLS platform escape rather than no
RLS, so a forgotten scope returns zero rows rather than another organization's. See
[ADR-028](adr/ADR-028-platform-capability-model.md).

There is deliberately no endpoint and no screen that writes this state: the `capabilities` CLI verb is
the only write surface, and in production it runs as a manual-trigger Container Apps job inside the
private network, recording an append-only audit row — naming the accountable operator — for every
change.

## Request flow (CQRS pipeline)

The application pattern is **CQRS with vertical slices**. An endpoint binds the request, dispatches a
command or query record through a hand-rolled `ISender`, and maps the result — nothing more. The
dispatcher runs a decorator pipeline in pinned order (telemetry outermost, then validation, then the
handler). Each slice has one colocated FluentValidation validator — the single validation home.
Commands mutate only through domain services; queries read projections or SQL within their own
module. Endpoints are minimal APIs only (`TypedResults`), no MVC controllers; there is no MediatR and
no AutoMapper. See [ADR-005](adr/ADR-005-cqrs-owned-dispatcher-no-mediatr.md).

## Errors and observability

Every error response — CQRS slices and the auth endpoints alike — is built by **one factory**,
`ProblemResults` (`SharedKernel.Endpoints`), which stamps a machine-readable `code` and a
`correlationId` (the W3C trace id, the same value Application Insights indexes as `operation_Id`) on
every response; an architecture test fails the build on any direct `Results.Problem` /
`TypedResults.Problem` call elsewhere. A terminal exception handler, registered last, claims anything
the typed handlers decline and returns a generic 500 carrying only that reference — never the
exception message, type, or stack trace. `ILogger` output shares the tracing pipeline's OpenTelemetry
exporter, so the correlation id an operator sees on screen is directly searchable in Application
Insights once deployed. See [ADR-025](adr/ADR-025-error-contract-and-observability.md) and the
[diagnostics runbook](runbooks/diagnostics.md).

## Organization isolation and security

PostgreSQL **row-level security is the organization-isolation boundary** — EF Core global query filters are
ergonomics layered on top, not the boundary. Organization context is set per-transaction with
`SET LOCAL app.org_id` (never session-level, which would leak across pooled connections); missing
context fails closed. Three database roles separate concerns: `leasebook_migrator` (owns the `public`
schema), `leasebook_app` (runtime, `FORCE ROW LEVEL SECURITY`), and `leasebook_ops` (read-only). The
runtime role holds no DDL privilege in `public` and none on the database; its single exception is the
`hangfire` job-storage schema it owns, described under Background work below. Every
org-scoped table is created through the migrations RLS helper (column + `USING`/`WITH CHECK` policy +
`FORCE` in one call), and a schema-guard test fails CI if any `org_id` table lacks its policy.
`FORCE` binds the migrator role too, so a migration that rewrites existing rows must lift and restore
it around the statement; an architecture test reads migration source and fails the build on an
unbracketed data rewrite, which would otherwise match no rows in silence. Portal
sub-org visibility (an owner sees only their properties) is enforced at the application layer rather
than by stacking more RLS policies — see [ADR-003](adr/ADR-003-portal-suborg-scoping-at-app-layer.md).

Layered on top of that organization boundary, the host applies defense-in-depth hardening: a middleware
that sets security response headers and a strict content-security policy on every response, rate
limiting on the authentication endpoints, config-gated multi-factor enforcement for admin accounts,
and encryption of sensitive authentication data at rest. These controls are environment- and
config-gated — permissive in Development and tests — and a non-Development environment fails fast at
startup if required security configuration is missing. The security model and reporting process are in
[SECURITY.md](../SECURITY.md).

## Frontend and the generated API client

The SPA is React 19 + TypeScript on Vite, with TanStack Query for server state. Reusable UI
primitives live in the design system (`web/src/design`); app-level shared components composed above
them (page scaffolds, modals, the record quick-switch) live in `web/src/components`; and
`web/src/lib` holds pure TypeScript utilities and hooks only. Money always renders through the
`<Money>` primitive with
tabular numerals and the organization's negative-display preference, and status is never conveyed by
color alone. The typed API client (`web/src/api/generated`) is **generated** by Hey API from the
host's OpenAPI document, never hand-edited. A build-time drift gate regenerates the client from a
build-time copy of the contract and fails CI if the committed files are stale, so the frontend and
backend contracts cannot silently diverge — see
[ADR-012](adr/ADR-012-openapi-client-drift-gate.md) and
[ADR-030](adr/ADR-030-hey-api-and-typescript-7.md).

`web/src/api` owns request **execution** as well as the generated client: one success rule
(`unwrap`) that turns a failed call into the mapped `ApiError`, one file-download helper
(`download`), and the error vocabulary the UI renders. Because reads run through that rule rather
than a hand-written throw, a failed read carries the server's `code` and `correlationId` into the UI
instead of a hardcoded string. A source-scanning architecture test fails the build if the success
rule, `createObjectURL`, a `document.cookie` read, or a raw `fetch(` appears under `web/src` outside
`web/src/api` — see the 2026-08-20 amendment to
[ADR-025](adr/ADR-025-error-contract-and-observability.md).

## Data and persistence

A **single `AppDbContext`**, owned by the host, discovers each module's `IEntityTypeConfiguration`
implementations by assembly scan; modules contribute mappings but do not each carry their own
context (one database, one transaction per request — the RLS boundary). See
[ADR-004](adr/ADR-004-single-appdbcontext-in-host.md). Migrations are authored in the host and
applied by the `leasebook_migrator` role through a one-shot migrator image — **never at app
startup**. Money is `decimal` in C# and `NUMERIC(14,2)` in Postgres, end to end, never floating
point. The journal, audit, property-ownership-transition, and statement-delivery tables are
append-only: the runtime role holds no `UPDATE`/`DELETE` grant on them, so a correction can only ever
be a linked reversal, a later transition, or a later recorded fact. Statement delivery is modeled that
way end to end — an immutable rendered artifact, the attempts that send it, and the append-only events
that say what became of each attempt — so current delivery status is computed from an attempt's latest
event rather than stored, and a provider acceptance followed by a bounce keeps both facts; see
[ADR-040](adr/ADR-040-statement-delivery-history.md). Every journal entry and audit row also carries a
durable actor — a user id, or the name of the system process that acted — and a write that declares
neither is refused rather than stored unattributed; see
[ADR-039](adr/ADR-039-durable-actor-attribution.md).

## Test execution

Every executable xUnit v3 project runs on Microsoft Testing Platform v2. The .NET 10 runner is selected
once in the repository's `global.json`, and test projects reference the explicit `xunit.v3.mtp-v2`
package; the VSTest adapter and `Microsoft.NET.Test.Sdk` compatibility path are intentionally absent.
The same projects are discovered by Visual Studio 2022 17.14 or later through its MTP Test Explorer
integration. CI uses the MTP console result plus retained TRX reports. See
[ADR-032](adr/ADR-032-microsoft-testing-platform-v2.md).

## Background work

Durable background jobs (statement generation/email, the nightly trust-equation sweep, future
webhook retries) run on **Hangfire backed by PostgreSQL** — no extra infrastructure. The first
integration is the **nightly invariant sweep**: it runs the trust-accounting correctness invariants
across every org at 07:00 UTC, logging each violation under a stable event id for alerting and
failing the run so the breach is also durable in job storage. It shares one code path with the
`check-invariants` CLI verb, so the command an engineer runs and the job that runs nightly can never
check different things.

Scheduling is config-gated (`Jobs:Enabled`) and **off by default** — enabled in production, so local
development, tests, and CI never start a job server. Hangfire's storage lives in its own `hangfire`
schema, owned by the runtime role because Hangfire installs and upgrades its own objects; it is
pre-created by [`infra/db/bootstrap.sql`](../infra/db/bootstrap.sql). The Hangfire **dashboard is
deliberately not mounted** (attack surface) — job state is observed through logs, alerts, and the
`leasebook_ops` read grant.

Redis is deliberately deferred until a concrete need appears. Every job must establish organization context
transactionally before touching data and throw if it is missing. See
[ADR-001](adr/ADR-001-background-job-scheduler.md) and [ADR-002](adr/ADR-002-defer-redis.md).

## Deployment

The production image serves the SPA and `/api` on the container's port `8080` and runs on Azure
Container Apps (East US 2), with secrets in Key Vault accessed by managed identity. Infrastructure is
declared as Bicep modules in [`infra/`](../infra) (see [`infra/README.md`](../infra/README.md)), and
CI compiles every template on each pull request.

The same executable also runs foreground operator CLI verbs and the build-time OpenAPI generator.
One explicit host lifecycle selects those mutually exclusive modes and owns their allowed startup
effects: CLI shares application composition but starts no HTTP or background host, while OpenAPI
generation composes the endpoint surface without database or deployment-dependent startup work. See
[ADR-042](adr/ADR-042-explicit-host-process-lifecycle.md).

Container probes are split by intent. `/api/health` is **liveness** — the process is up, and it touches
no dependency. `/api/health/ready` is **readiness**: it stays unavailable until two independent
preconditions hold — a startup probe has proven the capability seam readable, and the four fixed roles
have been seeded — so a replica that boots while the database is degraded, or entirely unreachable, is
held out of rotation and retried rather than serving traffic it cannot answer. Both preconditions are
retried in the background until they succeed. Neither one is enough on its own: a reachable seam says
nothing about whether roles exist, and a replica with no roles cannot authenticate anyone. Startup
work that would previously have killed the process on an unreachable database now logs and continues,
so the host binds and readiness — not the exit code — keeps the replica out of rotation; a genuine
server-side rejection such as a missing table or a revoked grant still fails the boot. See
[ADR-028](adr/ADR-028-platform-capability-model.md). Readiness is tuned for patience and liveness for
speed; a separate startup probe covers the pre-bind work (role seeding, registry validation, job
wiring) that happens before the host binds a port.

Migrations always run separately from the application, as the `leasebook_migrator` role and **never
at app startup** — but the two environments reach the database differently. Production's PostgreSQL
server is VNet-injected and has no public endpoint, so a hosted runner cannot reach it: migrations
run instead as a one-shot **Container Apps Job** inside the same environment, polled to a terminal
state before the app revision rolls. Development keeps a public, firewall-gated server and migrates
from the deploy runner. See
[ADR-027](adr/ADR-027-prod-private-networking-and-migration-job.md).

The full stack runs locally through Docker Compose — `./scripts/dev.ps1 app-up` brings up
database → migrate → seed → app.

## Related documents

- [README](../README.md) — overview, getting started, and the port map
- [`blueprint.md`](blueprint.md) — the committed architecture blueprint (tech defaults, RLS design,
  trust-accounting data model)
- [`accounting.md`](accounting.md) — the trust-accounting model in plain English
- [`ROADMAP.md`](ROADMAP.md) — shipped capabilities and high-level product direction
- [`adr/`](adr/) — architecture decision records (start with the [index](adr/README.md))
- [`runbooks/`](runbooks/) — local development, restore, and diagnostics runbooks
- [`AGENTS.md`](../AGENTS.md) — the cross-agent engineering constraints and non-negotiable invariants
