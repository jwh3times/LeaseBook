# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository state

LeaseBook is a property-management SaaS for small residential PMs, differentiated on **correct
trust accounting** (NC fiduciary standard), low click-depth UX, and flat pricing. The repo is
currently **planning-stage — no application code exists yet**. All work is governed by
`private/TODO.md`, the master build plan: milestones M0–M8 implement PRD Phase 1 and are sequenced
top-to-bottom; each milestone ends in a demonstrable state.

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

## Commands

There is no build system yet. When M0 scaffolding lands (.NET solution + Vite/React web app),
replace this section with the real commands (build, test, single-test filter, dev server,
migrations, seed). Do not guess commands before they exist.

## Planned architecture (private/TODO.md §1 is the full blueprint)

- **Modular monolith**: ASP.NET Core host + one project per module
  (`Accounting`, `Directory`, `Banking`, `Reporting`, `Operations`, `Payments`, `SharedKernel`,
  `Migrator`). Modules reference `SharedKernel` only; cross-module calls go through public
  `Contracts` interfaces so modules stay extractable. SPA lives in `web/`, infra in `infra/`
  (Bicep), ADRs in `docs/adr/`.
- **Application pattern**: CQRS with vertical slices inside each module. Endpoints are minimal
  APIs only (no MVC controllers) — per-module `IEndpointModule` registration, route groups,
  `TypedResults` — and stay thin: bind → dispatch → map result. Commands/queries go through a
  hand-rolled `ISender` dispatcher with a decorator pipeline; **FluentValidation** validators
  (one per command/query, colocated with the slice) run in that pipeline as the single
  validation home. **No MediatR, no AutoMapper** (both commercially licensed from
  v13/v15 onward) — don't add them out of habit. Commands mutate only through domain services;
  queries read projections/SQL directly.
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
