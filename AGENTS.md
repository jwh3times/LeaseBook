# AGENTS.md

This file provides guidance to Codex and other coding agents when working in this repository.
It is derived from `CLAUDE.md`; when the two files drift, preserve the project rules and update
both files intentionally.

## Repository State

LeaseBook is a property-management SaaS for small residential property managers, differentiated on
correct trust accounting and low click-depth UX. All milestone work is governed by
`private/TODO.md`, the master build plan. Milestones M0-M8 implement PRD Phase 1 and are sequenced
top-to-bottom; each milestone ends in a demonstrable state. Per-milestone plans and retrospectives
live in `private/planning/` as `m{N}_plan.md` and `m{N}_retro.md`; the m0-m7 retros are also
published (lightly scrubbed) in `docs/planning/`.

`private/TODO.md` checkboxes and `private/planning/*_retro.md` are the live source of truth. Consult
them directly; do not trust summaries, including this one, for current progress.

- M0-M7 are complete and merged to `main`: foundations; the trust-accounting engine; Directory; the
  tenant ledger action hub; Banking and Reconciliation; Owner Statements and Reporting; Bulk
  Operations; and the Migration toolkit and import-first onboarding.
- M8, Hardening, Compliance and Beta Launch, is the current frontier. It is partially shipped:
  `azure-infrastructure` specialist guidance, CI e2e run plus automated WCAG 2 AA accessibility gate
  from ADR-022, and visual-regression baselines for money-critical states from ADR-023. Remaining M8
  work is planned in `docs/ROADMAP.md`. `docs/ROADMAP.md` is public-safe; `private/TODO.md` remains
  canonical where they disagree.
- Operator-gated remainder is deferred and is not ordinary engineering work: Azure OIDC, ACR,
  Container App deploy wiring, live Key Vault and managed identity, the first PITR drill, and
  deployment-dependent telemetry/alerting.
- `Accounting`, `Directory`, `Banking`, `Reporting`, `Operations`, and `Migrator` are built.
  `Payments` is the remaining scaffolded shell for Phase 2.

The `private/` directory is gitignored, confidential, and local-only. It may be absent in a public
clone. It holds:

- `private/TODO.md`: master build plan (its section-1 architecture blueprint and Definition of
  Done have committed projections in `docs/blueprint.md` and CONTRIBUTING.md)
- `private/LeaseBook_PRD_v1.0.md`: product requirements and scope authority
- `private/claude_design_files/`: interactive UI prototype, design-system source of truth, and
  design/product analysis

Anything confidential, including pricing, strategy, customer identity, and internal analysis, belongs
in `private/` and must never be added to committed files. If `private/` is absent and the task depends
on it, ask the user for the relevant build-plan or PRD context before starting milestone work.

## Ground Rules

Verify, do not assume.

- Do not build on unverified assumptions. When a task depends on a fact that cannot be confirmed from
  code, docs, or a quick local check, stop and verify before designing against it.
- This is especially important for trust-accounting and domain facts: NCREC 58A .0116
  recordkeeping rules, basis behavior, posting-template lines, prototype golden figures, demo-seed
  numbers, EF/RLS/posting runtime behavior, and anything that can affect fiduciary correctness.
- Ground truth is usually obtainable locally:
  - Scope and intent: `private/LeaseBook_PRD_v1.0.md` and `private/claude_design_files/`
  - Built or decided work: `private/TODO.md` and `private/planning/*_retro.md`
  - Real figures and behavior: run `check-invariants`, golden/invariant/property suites, query the
    seeded demo org, or read the migration, RLS-helper, and posting-template code
- Designing a structure to discover an unknown at runtime is still building on an assumption. Verify
  the discovery against real data such as journal replay or the golden dataset.
- A sensible default for a genuinely low-stakes choice is fine. State the assumption and proceed.
  If being wrong would force rework, move a golden figure, or ship fiduciary incorrectness, verify or
  ask.

## Commands

Solution: `LeaseBook.slnx`. The SPA lives in `web/`. The project expects .NET 10 SDK, Node 26, and
Docker for local Postgres and integration tests.

Every bound port is mapped in the Port map section of the root `README.md`: inner-loop dev
`5373`/`5080`/`5632`, full Docker stack host port `8082`, container and production ingress `8080`,
and `LEASEBOOK_APP_PORT`/`LEASEBOOK_DB_PORT`/`LEASEBOOK_PGADMIN_PORT` overrides. Keep that table and
the configs it cites in sync when a port changes.

### Backend (.NET, run from repo root)

- Build: `dotnet build LeaseBook.slnx -c Debug`
- All tests: `dotnet test LeaseBook.slnx`
- One project: `dotnet test tests/LeaseBook.Tests.Integration/LeaseBook.Tests.Integration.csproj`
- Single test, xUnit v3: `dotnet test <proj> --filter "FullyQualifiedName~TenantIsolationTests"`
- Format check: `dotnet format --verify-no-changes --exclude src/LeaseBook.Web/Migrations`
- Run API on `:5080`: `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web`
- Integration tests use Testcontainers. Docker must be running; local compose is not required.

### Database (run from repo root)

Run `dotnet tool restore` once for `dotnet-ef`.

- Local Postgres: `./scripts/dev.ps1 up`, `down`, `reset-db`, or `psql`
- Full stack in Docker: `./scripts/dev.ps1 app-up`, `app-down`, or `app-logs`
- Add migration: `dotnet ef migrations add <Name> --project src/LeaseBook.Web`
- Apply migrations as migrator role: `dotnet ef database update --project src/LeaseBook.Web`
- Seed demo org: `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- seed --org demo`
- Check invariants: `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- check-invariants --org demo`
- Check all org invariants: `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- check-invariants --all`

### Web (run from `web/`)

- Dev server on `:5373`, proxying `/api` to `:5080`: `npm run dev`
- Lint: `npm run lint`
- Typecheck: `npm run typecheck`
- Tests: `npm run test`
- Build: `npm run build`
- Single test: `npm run test -- src/design/formatMoney.test.ts`
- Regenerate API client, with API host running on `:5080`: `npm run api:generate`
- e2e, against a seeded host: `npm run e2e`
- e2e accessibility gate only: `npm run e2e -- a11y.spec.ts`

### Container

- Build image: `docker build -t leasebook .`
- Run with `ConnectionStrings__Default` set. The container serves SPA and `/api` on internal port
  `8080`.
- Run the whole product locally, including db, migrate, seed, and app:
  `./scripts/dev.ps1 app-up`. Compose `full` profile serves at `http://localhost:8082`.
- Migrations run as the migrator role through a one-shot `migrator` target image, never at app
  startup.

## Domain Guidance Files

Claude specialist-agent guidance lives under `.claude/agents/`. Codex cannot assume those agents are
available as executable subagents, but their instructions are still important project knowledge. Before
editing one of these domains, read the corresponding file and apply its rules unless a higher-level
invariant in this file conflicts.

| Work type                                                               | Read first                               |
| ----------------------------------------------------------------------- | ---------------------------------------- |
| .NET features, endpoints, commands/queries, integration tests           | `.claude/agents/dotnet-api.md`           |
| React components, hooks, design tokens, frontend tests                  | `.claude/agents/react-frontend.md`       |
| Migrations, RLS policies, DB schema design, Postgres queries            | `.claude/agents/postgres-specialist.md`  |
| Accounting posting logic, journal entries, trust equation changes       | `.claude/agents/trust-accounting.md`     |
| Reviewing a diff for correctness bugs before merging                    | `.claude/agents/code-reviewer.md`        |
| Azure infra, Bicep, deploy workflows, Key Vault, managed identity, PITR | `.claude/agents/azure-infrastructure.md` |
| Documentation drift after source changes                                | `.claude/agents/docs-updater.md`         |

Cross-cutting rules, module boundaries, tenancy model, and trust-accounting invariants in this file
apply to all work. If a domain guidance file conflicts with these invariants, the invariant wins.

Claude's read-only Stop hook in `.claude/settings.json` detects documentation drift for Claude Code
sessions. Codex should not assume that hook runs. When source changes affect docs, ports, ADR-worthy
decisions, user workflows, commands, or business events, check documentation drift manually and update
the relevant docs in the same change.

## Architecture

`docs/blueprint.md` is the committed blueprint (`private/TODO.md` section 1 is canonical where
they disagree). The patterns below are established and test-enforced in built modules and bind
future modules.

- Modular monolith: ASP.NET Core host plus one project per module: `Accounting`, `Directory`,
  `Banking`, `Reporting`, `Operations`, `Payments`, `SharedKernel`, and `Migrator`. Modules reference
  `SharedKernel` only, enforced by `ModuleBoundaryTests`.
- Cross-module calls go through `Contracts` ports so modules stay extractable. A feature module must
  never read another module's tables or data directly: no cross-module SQL, no cross-module LINQ, and
  no referencing another module's entity types.
- Cross-module reads use a consumer-owned port: an interface declared in the consuming module's
  `Contracts`, implemented by a thin host adapter that delegates to the producing module via
  `ISender`.
- Ports expose batch reads that return maps, never per-id reads. Adapters run on the ambient RLS
  transaction and must not open a new connection.
- `SharedKernel` stays pure cross-cutting primitives. Module contracts do not live there.
- Within a module, `db.Database.SqlQuery<T>` is acceptable for that module's own analytical reads.
  Prefer EF LINQ for simple reads; reserve raw SQL for window functions, `FILTER`, trigram queries, or
  other queries LINQ cannot express.
- The dedicated reporting/read layer is the sole intentional exception to the no-cross-module-read
  rule. It may read across schemas on purpose and records its own ADR.
- Application pattern: CQRS with vertical slices. One file per feature contains the command/query
  record, `AbstractValidator`, and handler. Minimal APIs only through `IEndpointModule` and
  `TypedResults`; hand-rolled `ISender` dispatcher; no MediatR and no AutoMapper.
- Accounting is the core module and gets the highest test rigor. It uses a double-entry journal:
  `journal_entries` plus `journal_lines`, written only through posting templates keyed to business
  events such as `RentCharged`, `DepositApplied`, and `OwnerDisbursed`.
- Dual-basis posting: lines are tagged cash/accrual/both, so each accounting basis is a query, not a
  transformation.
- Tenant ledgers, owner ledgers, bank registers, and statements are read-model projections of the
  journal, never independently maintained state.
- Stack is PRD-locked: .NET current LTS, React and TypeScript with Vite, PostgreSQL, Azure Container
  Apps in East US 2, Key Vault and managed identity. Defaults such as EF Core, Bicep, QuestPDF, and
  TanStack Query are tabled in `docs/blueprint.md`. Changing one requires an ADR.

## Non-Negotiable Invariants

Violating these is a correctness bug, not a style issue.

### Trust Accounting

- PM income is isolated from owner income at the data-model level by account class, never by a
  reporting filter. No `PMIncome` line may be reachable by any owner-statement query.
- Security deposits and prepayments are liabilities until applied. Income recognition fires only on
  application, with identical behavior in cash and accrual modes.
- Ledgers are append-only. Corrections are linked reversal entries; posted rows are never updated or
  deleted. The runtime DB role has no `UPDATE` or `DELETE` grant on journal/audit tables.
- Every journal entry balances, with debits equal to credits, per basis.
- The trust equation must hold for every trust account at all times:
  bank book balance = owner equity + deposit liabilities + held PM fees.
- Money is `decimal` in C# and `NUMERIC(14,2)` in Postgres. Never use `float` or `double` for money.
- Reconciliation finalize locks the accounting period. Postings into locked periods are rejected.

### Multi-Tenancy

- Postgres RLS is the security boundary. EF global query filters are ergonomics only.
- Org context is set with `SET LOCAL app.org_id` inside the transaction. Never use session-level
  `SET`, because pooled connections would leak context.
- Missing org context fails closed.
- There are three DB roles:
  - `leasebook_migrator`: schema owner, migrations only
  - `leasebook_app`: runtime, RLS-subject via `FORCE ROW LEVEL SECURITY`
  - `leasebook_ops`: read-only
- Every new org-scoped table goes through the migrations RLS helper: column, `USING`/`WITH CHECK`
  policy, and `FORCE ROW LEVEL SECURITY` in one call.
- A schema guard test fails CI if any `org_id` table lacks its policy.
- Background jobs must establish org context explicitly and throw when it is missing.

### UX Contract

These flows are instrumented in telemetry; regressions fail the release checklist.

- Record a tenant payment in no more than 3 interactions.
- Owner ending balances are visible at 0 clicks.
- Uncleared trust items are visible in no more than 1 click.
- Start reconciliation in no more than 2 clicks, completed in place.
- UI must use design tokens and primitives ported from the prototype.
- Money renders with tabular numerals.
- Status is never conveyed by color alone.

## Working Conventions

- Follow `private/TODO.md` order. Check off boxes as tasks complete and keep it current. Scope
  changes are edits to the plan, not side conversations. Items marked `GATE` block the work below
  them until resolved.
- The Definition of Done (published in CONTRIBUTING.md; canonical at the bottom of
  `private/TODO.md`) applies to every task: tests at the right altitude, audit/telemetry events on
  money-touching paths, empty/loading/error states, keyboard path, and demoability on the seed org.
- Scope discipline: the PRD exclusions are rejected on sight through Phase 5. These include HOA,
  commercial, short-term rentals, native mobile, proprietary screening/listing engines, full GL
  bookkeeping, AI features, and public API. Additions require a formal scope change recorded in
  `private/TODO.md`.
- ADRs live in `docs/adr/`. Every deviation from a `docs/blueprint.md` default gets a short
  ADR.
- Seed data is sacred. The demo dataset, ported from the prototype's `data.jsx`, doubles as the
  golden-file test fixture. Its figures reconcile to the cent and validate the accounting engine.
  Do not casually edit seed numbers. If they change, golden tests change deliberately in the same
  work.
- Accounting-adjacent changes must run the invariant, property-based, and golden-file suites.
- If a budgeted UX flow changes, add or update the corresponding Playwright e2e coverage.
- Before .NET work, read `.claude/agents/dotnet-api.md`.
- Before React/UI work, read `.claude/agents/react-frontend.md`.
- Before schema, migration, RLS, or query work, read `.claude/agents/postgres-specialist.md`.
- Before accounting posting or trust-equation work, read `.claude/agents/trust-accounting.md`.
- Before Azure infra, Bicep, deploy workflow, Key Vault, managed identity, or PITR work, read
  `.claude/agents/azure-infrastructure.md`.
- Before pre-merge review, read `.claude/agents/code-reviewer.md` and lead with findings.
- When documentation accuracy is affected or in doubt, read `.claude/agents/docs-updater.md` and
  update only the docs that actually drifted.

## Agent Operating Notes

- Use `rg` or `rg --files` for searches whenever available.
- Do not overwrite user changes. Check `git status --short` before editing and review existing
  diffs in files you need to touch.
- Keep edits scoped to the requested work and existing module boundaries.
- Prefer existing repo patterns, helpers, tests, and design primitives over new abstractions.
- Add tests proportional to risk. Money, tenancy, RLS, posting, and migration changes require higher
  confidence than presentational changes.
- Do not put confidential `private/` details into committed files, PR descriptions, public docs, or
  generated output.
