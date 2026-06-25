# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository state

LeaseBook is a property-management SaaS for small residential PMs, differentiated on **correct
trust accounting** (NC fiduciary standard), low click-depth UX, and flat pricing. All work is
governed by `private/TODO.md`, the master build plan: milestones M0–M8 implement PRD Phase 1 and
are sequenced top-to-bottom; each milestone ends in a demonstrable state. Per-milestone plans and
retrospectives live in `private/planning/` (`m{N}_plan.md` / `m{N}_retro.md`).

**Progress (the `private/TODO.md` checkboxes and `private/planning/*_retro.md` are the live source of
truth — consult them, don't trust a summary):**

- **M0–M4 are complete and merged to `main`** — built and tested: foundations (solution, RLS, auth,
  CI, design-system port), the trust-accounting engine (double-entry journal, posting-template
  catalog, dual-basis ledgers, invariant/property/golden-file harness), the Directory module
  (owners/properties/units/tenants/leases-lite, index views, ⌘K palette, dashboard v1), the tenant
  ledger action hub (inline composer, deposit apply, void/reverse, audit drawer), and **Banking &
  Reconciliation** (bank register + clearance over the immutable journal, reconcile-in-place → $0 →
  finalize → per-account lock + immutable report, CSV statement import + auto-match + dedup, and the
  composite `(org_id, id)` journal-dimension FKs from the ADR-008 revisit).
- **M5 (Owner Statements & Reporting) is the current frontier.** An **M5-prep close-out pass** first
  cleared five carried M0–M4 loose ends — the dashboard uncleared KPI wired to the live register, the
  `is_system` exclusion behind a `NotSystem()` funnel + behavioral test, guarded bank-account
  deactivation, the project subagents in `.claude/agents/`, and the Node pin — leaving the M5 statement
  engine + report catalog as the next build (see `private/TODO.md` §M5).
- **Operator-gated remainder of M0** (deferred — not engineering work): Azure OIDC/ACR/Container App
  deploy wiring, live Key Vault + managed identity, and the first PITR drill (M8 schedules it).
- The `Banking` module is built (M4); `Reporting`, `Operations`, and `Payments` are scaffolded shells
  awaiting their milestones (M5–M8 / Phase 2); `Migrator` is a placeholder (M7).

The `private/` directory is **gitignored (confidential, local-only)** and will be absent in a
public clone. It holds everything not meant for the public repo:

- `private/TODO.md` — the master build plan (lives here because it contains pricing/strategy
  detail derived from the PRD)
- `private/LeaseBook_PRD_v1.0.md` — product requirements; scope authority
- `private/claude_design_files/` — interactive UI prototype (design-system source of truth)
  and the Design & Product Analysis Report (priority rationale)

The rule: anything confidential — pricing, strategy, customer identity, internal analyses —
goes in `private/`, never in committed files. This file is committed, so it carries only the
engineering constraints, not the business specifics behind them. If `private/` is absent in
your environment, ask the user for the build plan before starting milestone work rather than
reconstructing it from this summary.

## Ground Rules — verify, don't assume

- **Don't build on unverified assumptions — check or ask first.** When a task depends on a fact you
  can't confirm from the code, the docs, or a quick check — especially **trust-accounting / domain
  facts** (an NCREC 58A .0116 recordkeeping rule, how a basis treats an event, a posting template's
  per-basis lines, the prototype's golden figures, the demo seed's _actual_ numbers, EF/RLS/posting
  runtime behavior) — stop and verify before designing against a guess. Ground truth here is almost
  always _obtainable locally_, so get it **before** writing the implementation, not as a check deferred
  to the end:
  - scope/intent → `private/LeaseBook_PRD_v1.0.md` (scope authority) and `private/claude_design_files/`
    (design-system + screen source of truth);
  - what's actually built or decided → the `private/TODO.md` checkboxes and `private/planning/*_retro.md`
    are the **live source of truth — consult them, never a prose summary** (including the "Repository
    state" summary in this very file, which lags reality between milestones);
  - real figures/behavior → run `check-invariants` and the golden / invariant / property suites, or
    query the seeded demo org; read the migration / RLS-helper / posting-template code for actual
    behavior.
    Designing a structure to "discover" an unknown at runtime is still building on an assumption — verify
    the discovery against real data (a real journal replay, the golden dataset).
- A sensible default for a genuinely low-stakes choice is fine — state it and proceed. The bar: would
  being wrong force a rework, move a golden figure, or ship something fiduciarily incorrect? If yes,
  it's load-bearing — verify or ask.

## Commands

Solution is `LeaseBook.slnx`; the SPA lives in `web/`. .NET 10 SDK + Node 26, Docker for local
Postgres and integration tests. Every port the project binds — inner-loop dev (`5373`/`5080`/`5632`),
the full Docker stack host port (`8082`; container + prod ingress stay `8080`), and the
`LEASEBOOK_APP_PORT`/`LEASEBOOK_DB_PORT`/`LEASEBOOK_PGADMIN_PORT` overrides — is mapped in the
**Port map** section of the root `README.md`; keep that table and the configs it cites
(launchSettings, vite, compose, Dockerfile, bicep) in sync when a port changes. Host ports are
offset from the defaults so the stack runs alongside other local projects.

**Backend (.NET, run from repo root):**

- Build: `dotnet build LeaseBook.slnx -c Debug` (nullable + warnings-as-errors)
- All tests: `dotnet test LeaseBook.slnx`
- One project: `dotnet test tests/LeaseBook.Tests.Integration/LeaseBook.Tests.Integration.csproj`
- Single test (xUnit v3): `dotnet test <proj> --filter "FullyQualifiedName~TenantIsolationTests"`
- Format check (CI gate): `dotnet format --verify-no-changes --exclude src/LeaseBook.Web/Migrations`
- Run API (dev, :5080): `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web`
- Integration tests use Testcontainers — Docker must be running; they don't need local compose.

**Database (run from repo root; `dotnet tool restore` once for `dotnet-ef`):**

- Local Postgres: `./scripts/dev.ps1 up` | `down` | `reset-db` | `psql` (see docs/runbooks/local-dev.md)
- Add migration: `dotnet ef migrations add <Name> --project src/LeaseBook.Web`
- Apply migrations (migrator role): `dotnet ef database update --project src/LeaseBook.Web`
- Seed demo org: `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- seed --org demo`
- Check invariants: `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- check-invariants --org demo` (or `--all`)

**Web (run from `web/`):**

- Dev server (:5373, proxies `/api` → :5080): `npm run dev`
- Lint / typecheck / test / build: `npm run lint` · `npm run typecheck` · `npm run test` · `npm run build`
- Single test: `npm run test -- src/design/formatMoney.test.ts`
- Regenerate API client (host must be running on :5080): `npm run api:generate`
- e2e (Playwright; specs in `web/e2e/`, run against a seeded host): `npm run e2e`

**Container:** `docker build -t leasebook .` then run with `ConnectionStrings__Default` set — serves the
SPA and `/api` on port 8080 (the container's internal port). To run the **whole product** locally
(db + migrate + seed + app): `./scripts/dev.ps1 app-up` (Compose `full` profile →
<http://localhost:8082>; see docs/runbooks/local-dev.md).
Migrations run as the migrator role via a one-shot `migrator`-target image (EF bundle), never at app startup.

## Specialist agents

Invoke the relevant specialist **before** working in its domain — don't rely on this file for
implementation patterns, only for invariants and authority. Each agent has file-cited examples,
banned-pattern tables, and domain rules that supersede any summary here.

| Work type                                                         | Agent                                    |
| ----------------------------------------------------------------- | ---------------------------------------- |
| .NET features, endpoints, commands/queries, integration tests     | `dotnet-api`                             |
| React components, hooks, design tokens, frontend tests            | `react-frontend`                         |
| Migrations, RLS policies, DB schema design, Postgres queries      | `postgres-specialist`                    |
| Accounting posting logic, journal entries, trust equation changes | `trust-accounting`                       |
| Reviewing a diff for correctness bugs before merging              | `code-reviewer`                          |
| Documentation drift after source changes                          | `docs-updater` (auto-runs via Stop hook) |

Cross-cutting rules (non-negotiable invariants, module boundary, tenancy model) are authoritative
here in CLAUDE.md and apply to **all agents**. When an agent's guidance and an invariant here
conflict, the invariant wins.

## Architecture (private/TODO.md §1 is the full blueprint)

The patterns below are established and test-enforced in the built modules (Accounting, Directory,
SharedKernel, Web host); they bind the unbuilt modules (M4+) equally.

- **Modular monolith**: ASP.NET Core host + one project per module
  (`Accounting`, `Directory`, `Banking`, `Reporting`, `Operations`, `Payments`, `SharedKernel`,
  `Migrator`). Modules reference `SharedKernel` only (enforced absolutely by `ModuleBoundaryTests`);
  cross-module calls go through `Contracts` ports per the boundary rule below, so modules stay
  extractable. SPA lives in `web/`, infra in `infra/` (Bicep), ADRs in `docs/adr/`.
- **Cross-module boundary (ADR-007 — applies to every milestone, not just M2)**: a feature module
  **never reads another module's tables or data directly** — no cross-module SQL, no cross-module
  LINQ, no referencing another module's entity types. A cross-module read goes through a
  **consumer-owned port**: an interface declared in the _consuming_ module's `Contracts`, implemented
  by a thin **host adapter** that delegates to the producing module via `ISender`. Ports expose
  **batch** reads (return a map), never per-id reads, and adapters run on the ambient RLS transaction
  (no new connection). `SharedKernel` stays pure cross-cutting primitives — module contracts do not
  live there. _Within_ a module, `db.Database.SqlQuery<T>` for that module's **own** analytical reads
  is fine (prefer EF LINQ for simple reads; reserve raw SQL for window-function / `FILTER` / trigram
  queries LINQ can't express — this crosses no boundary). **The sole exception is a dedicated
  reporting/read layer** (the M5 statement engine and any future read-model schema), which may read
  across the schema on purpose and records its own ADR. The assembly half of this rule is
  test-enforced; the no-cross-module-SQL half is a code-review rule (NetArchTest can't see SQL strings).
- **Application pattern**: CQRS with vertical slices — one file per feature containing the
  command/query record, `AbstractValidator`, and handler. Minimal APIs only (`IEndpointModule` +
  `TypedResults`); hand-rolled `ISender` dispatcher; no MediatR, no AutoMapper. See the
  **`dotnet-api` specialist agent** for the full slice anatomy, endpoint shape, test conventions,
  and banned-pattern table.
- **Accounting is the core module** and gets the highest test rigor: a double-entry journal
  (`journal_entries` + `journal_lines`) written only through posting templates keyed to
  business events (`RentCharged`, `DepositApplied`, `OwnerDisbursed`, …). Dual-basis posting:
  lines are tagged cash/accrual/both, so each accounting basis is a query, not a transformation.
  Tenant ledgers, owner ledgers, bank registers, and statements are all read-model projections
  of the journal — never independently maintained state.
- **Stack** (PRD-locked): .NET current LTS, React + TypeScript (Vite), PostgreSQL, Azure
  Container Apps (East US 2), Key Vault + managed identity. Defaults (EF Core, Bicep,
  QuestPDF, TanStack Query, etc.) are tabled in private/TODO.md §1 — changing one requires an ADR.

## Non-negotiable invariants (violating these is a correctness bug, not a style issue)

Trust accounting:

- PM income is isolated from owner income **at the data-model level** (account class), never by
  a reporting filter. No `PMIncome` line may be reachable by any owner-statement query.
- Security deposits and prepayments are **liabilities until applied**; income recognition fires
  only on application — identical behavior in cash and accrual modes.
- Ledgers are **append-only**: corrections are linked reversal entries; posted rows are never
  updated or deleted. The runtime DB role has no UPDATE/DELETE grant on journal/audit tables.
- Every journal entry balances (Σ debits = Σ credits) per basis. The trust equation —
  bank book balance = Σ owner equity + Σ deposit liabilities + held PM fees — must hold for
  every trust account at all times; it is continuously tested.
- Money is `decimal` in C# / `NUMERIC(14,2)` in Postgres. Never float/double.
- Reconciliation finalize locks the accounting period; postings into locked periods are rejected.

Multi-tenancy (full design in private/TODO.md §1 "Multi-tenancy & row-level security"):

- Postgres **RLS is the security boundary**; EF global query filters are ergonomics only.
- Org context is set with `SET LOCAL app.org_id` inside the transaction (never session-level
  `SET` — pooled connections would leak context). Missing context fails closed.
- Three DB roles: `leasebook_migrator` (schema owner, migrations only), `leasebook_app`
  (runtime, RLS-subject via `FORCE ROW LEVEL SECURITY`), `leasebook_ops` (read-only).
- Every new org-scoped table goes through the migrations RLS helper (column + `USING`/`WITH
CHECK` policy + FORCE in one call); a schema guard test fails CI if any `org_id` table lacks
  its policy.
- Background jobs must establish org context explicitly and throw when it's missing.

UX contract (instrumented in telemetry; regressions fail the release checklist):

- Record a tenant payment ≤ 3 interactions · owner ending balances visible at 0 clicks ·
  uncleared trust items ≤ 1 click · start reconciliation ≤ 2 clicks, completed in place.
- UI must use the design tokens and primitives ported from the prototype (M0.5); money renders
  with tabular numerals; status is never conveyed by color alone.

## Working conventions

- **Follow private/TODO.md order.** Check off boxes as tasks complete; keep it current — it is the
  living plan, and scope changes are edits to it, not side conversations. Items marked
  **🚧 GATE** block the work below them until resolved.
- The **Definition of Done** at the bottom of private/TODO.md applies to every task (tests at the right
  altitude, audit/telemetry events on money-touching paths, empty/loading/error states,
  keyboard path, demoable on the seed org).
- **Scope discipline**: the PRD's exclusions (HOA, commercial, short-term rentals, native
  mobile, proprietary screening/listing engines, full GL bookkeeping, AI features, public API)
  are rejected on sight through Phase 5; additions require a formal scope change recorded in
  private/TODO.md.
- **ADRs** (`docs/adr/`): every deviation from a private/TODO.md §1 default gets a short ADR; several
  are pre-assigned in private/TODO.md (job scheduler choice, Redis deferral, portal scoping).
- **Seed data is sacred**: the demo dataset (ported from the prototype's `data.jsx`) doubles as
  the golden-file test fixture — its figures reconcile to the cent and the accounting engine is
  validated against them. Don't casually edit seed numbers; if they change, the golden tests
  change with them, deliberately.
- Accounting-adjacent changes must run the invariant/property-based/golden-file suites; if a
  budgeted UX flow changed, the corresponding Playwright e2e must cover it.
- **Invoke the specialist agent for the domain you're working in** before writing code (see the
  Specialist agents table above). For .NET work: `dotnet-api`. For React/UI: `react-frontend`.
  For schema/migrations: `postgres-specialist`. For accounting logic: `trust-accounting`. For
  pre-merge review: `code-reviewer`.
- **`docs-updater` runs automatically at every session end** via the Stop hook in
  `.claude/settings.json` — it checks only files that changed and fixes drift in place. Invoke
  it proactively mid-session if documentation accuracy is in doubt (new ADR candidate, port
  change, new business event added).
