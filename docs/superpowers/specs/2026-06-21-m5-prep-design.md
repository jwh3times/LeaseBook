# M5-prep — close-out of carried M0–M4 loose ends (design)

- **Date:** 2026-06-21
- **Status:** Approved (design) — pending writing-plans
- **Scope authority:** `private/TODO.md` → `## M5-prep` section
- **Constraints:** `CLAUDE.md` (non-negotiable invariants, DoD), `docs/adr/` (ADR-007 boundary)

## Context

An M0–M4 documentation gap review produced a `## M5-prep` checklist in `private/TODO.md`. This spec
covers the **five locally-verifiable items** chosen for this implementation pass. Two items
(nightly Hangfire trust-equation sweep; click-budget release-gate) are **deployment/operator-gated**
— they cannot be meaningfully completed in a local/headless env (no deployed scheduler/alerting, no
local App Insights) — and are explicitly **out of scope here**; this pass reframes them in the TODO
as operator-gated, mirroring M0's deferred Azure items.

**No correctness bug is being fixed.** These are one visible UI loose end plus hardening the codebase
currently leaves to convention. Expected to ship with **zero schema migrations** (all touched columns
already exist), like M3.

## In scope (5 items)

### 1. Dashboard `uncleared` KPI → live register

**Problem.** `DashboardService` returns `Uncleared: 0m / UnclearedCount: 0`
(`src/LeaseBook.Web/Dashboard/DashboardService.cs:74`) with a stale action item ("Uncleared items
appear once the bank register lands (M4)", line 65). M4 shipped the register; the live source exists.

**Design.**
- Extend `GetBankBalances` (`src/LeaseBook.Modules.Accounting/Features/Ledgers/GetBankBalances.cs`)
  to add `UnclearedCount` per row — an additive count of uncleared bank lines per account in the
  existing own-module SQL (`BankBalanceRow` gains `int UnclearedCount`; the SQL row + projection
  follow). This same field powers item 3's guard port, so both items share one query change.
- `DashboardService`: `Uncleared = Σ rows.Uncleared`, `UnclearedCount = Σ rows.UnclearedCount`;
  remove the hardcoded `0`/`0`. Rewrite the "reconciliation-due" action item to a computed
  "N uncleared item(s) to reconcile" → `/banking` (or a "reconciled / nothing uncleared" state when 0).
- Add per-account `UnclearedCount` to `DashboardBankRow` so the M2.4 "Trust accounts summary card
  with uncleared counts" (also currently 0) is honest.
- Update `DashboardTests`: the KPI is now the honest sum across `GetBankBalances`, so assert
  **structurally** — `Kpis.UnclearedCount == Σ Banks.Rows.UnclearedCount` and
  `Kpis.Uncleared == Σ Banks.Rows.Uncleared` (proves it is computed, not hardcoded) — plus the
  golden-locked per-account figure **Operating Trust = 3 uncleared**. Note: PM-Operating clearance is
  *unseeded*, and absence of a `bank_line_status` row ≡ uncleared, so PM-Operating lines read as
  **uncleared, not 0** (correcting an earlier draft of this spec); the exact total is derived from the
  seed during implementation by reading `GoldenFileTests`/`DemoBankClearingSeed`, not guessed here.
  Regenerate `web/src/api/schema.d.ts` (KPI/bank-row shape changed) and update the web dashboard render
  (the `BankSummary` per-account count; the KPI StatCard already binds `kpis.uncleared`/`unclearedCount`)
  and its test.

**Tests.** `DashboardTests` (real figures), `GetBankBalances` count assertion, web `DashboardPage`
test for the rendered count, schema drift gate (ADR-012) green.

### 2. `is_system` guard (hardening) — m2_retro known-limitation #2

**Problem.** Each roster read inlines `.Where(o => !o.IsSystem)` (e.g. `ListOwners.cs:24`); nothing
fails CI if a new directory read forgets it and leaks the "All other owners" roll-up / synthetic
deposit-aggregate tenants.

**Design.**
- Add a blessed funnel: `NotSystem()` `IQueryable` extension(s) for `Owner` and `Tenant` (in
  Directory, alongside the existing `Features/Shared` helpers). Refactor existing roster/search/detail
  reads (`ListOwners`, `ListTenants`, `Search`, any detail read that enumerates) through it.
- Add a **behavioral integration test**: hit the owners + tenants **list and search** endpoints on the
  seeded org and assert the known system-aggregate rows never appear in results. A NetArchTest cannot
  inspect LINQ `Where` clauses, so an endpoint-level behavioral guard is the correct altitude; the
  funnel + this test together are the guard.

**Tests.** New `Tests.Integration` case (aggregate-leakage assertion across list/search endpoints);
existing golden/directory tests stay green.

### 3. Bank-account deactivation — M2-E9 (**guard on uncleared**, per decision)

**Problem.** `BankAccount.IsActive` exists but nothing flips it; the doc comment
(`BankAccount.cs:9,26`) wrongly says deactivation "lands in M4."

**Design.**
- **Command:** `SetBankAccountActive(Guid Id, bool IsActive)` in
  `Directory/Features/BankAccounts/`, mirroring `UpdateBankAccount`. Reactivation (`true`) is always
  allowed. Deactivation (`false`) is **guarded**: it is blocked while the account has uncleared items.
- **Cross-module guard (ADR-007).** The clearance state lives in Accounting (`bank_line_status`), so
  Directory consults Accounting through a **consumer-owned port** declared in `Directory.Contracts`
  (e.g. `IBankClearanceStatus.UnclearedCountsAsync(ct)` → `IReadOnlyDictionary<Guid,int>`, a **batch**
  map per ADR-007). A host adapter in `src/LeaseBook.Web/Adapters/` implements it by dispatching
  Accounting's `GetBankBalances` (now carrying `UnclearedCount`) via `ISender` on the ambient RLS
  transaction. Directory **never** reads `bank_line_status`/`journal_lines`.
- **Result/error path.** The command returns a typed result
  (`Ok(BankAccountResponse) | NotFound | BlockedUncleared`); the endpoint maps `BlockedUncleared` →
  `TypedResults.Conflict` with a ProblemDetails (`bank_account_has_uncleared`,
  "Clear or reconcile outstanding items before deactivating."). This follows Directory's existing
  return-value → `TypedResults` convention (no new cross-module exception handler;
  `AccountingExceptionHandler` stays accounting-only).
- **Picker filtering.** New-posting pickers exclude inactive accounts: composer bank select,
  apply-modal banks-by-purpose, bank-adjustment/transfer targets, CSV-import target. Implement via an
  `activeOnly` option on the bank list read (or a dedicated active-only list) consumed by those
  surfaces. **Read-side surfaces unchanged** (register, reconcile, statements still include the
  account) — though with the guard, a deactivated account has zero uncleared items by construction.
- **Settings UI.** Active/Inactive **badge (icon + label, never color alone)** + a
  Deactivate/Reactivate button per account on `web/src/features/settings/SettingsPage.tsx`. The
  blocked-deactivation 409 surfaces inline ("clear outstanding items first").
- **Doc fix.** Correct the stale `BankAccount.cs` comment (and the `CreateBankAccount`/`UpdateBankAccount`
  "deactivation is M4" notes) to describe the shipped behavior.

**Tests.** Command unit/integration: deactivate-blocked-when-uncleared (409), deactivate-allowed-when-clean,
reactivate. Port/adapter boundary stays clean (`ModuleBoundaryTests`). Web SettingsPage test for the
toggle + blocked state. Picker tests assert inactive accounts are absent from new-posting selectors.

### 4. `.claude/agents/` subagents — author all three top-value

**Problem.** `.claude/agents/` is empty; TODO.md §Documentation says author "once the code they cite
is stable" — M0–M4 is now stable/merged.

**Design.** Author three agents **fresh from LeaseBook's own invariants/ADRs** (adapt, do not copy
apexracers — that repo uses MVC + direct DbContext + startup migrations, all of which LeaseBook
rejects):
- `code-reviewer.md` — flags the invariant violations that are correctness bugs: ADR-007 cross-module
  SQL/LINQ, org-scoped table missing its RLS policy/FORCE, `float`/`double` money, mutated/deleted
  ledger rows, MediatR/AutoMapper, MVC controllers. Cites real modules/ADRs.
- `trust-accounting.md` (opus) — journal / posting-template catalog / dual-basis / trust-equation
  specialist; the differentiator.
- `rls-tenant-isolation.md` (opus) — `SET LOCAL` fail-closed, three-role grants, `FORCE ROW LEVEL
  SECURITY`, schema-guard coverage, cross-org leakage probing.

Each references concrete paths (e.g. `PostingService`, `Rls.EnableOrgRls`, `SchemaGuardTests`) that
exist today. No code change; these are agent definition files.

**Verification.** Files parse as valid agent definitions (frontmatter + body); content references are
spot-checked against current code.

### 5. Node pin + `.nvmrc`

**Problem.** CI/CLAUDE pin Node 26 (`.github/workflows/ci.yml:41,63`) but `web/package.json`
`engines.node` floor is `>=22.14.0`; no `.nvmrc`.

**Design.** Raise `web/package.json` `engines.node` to `>=26.0.0` (matching the CI `node-version: '26'`
pin) and add a repo-root `.nvmrc` containing `26` (read by nvm/fnm/Volta; the repo is polyglot so root
is the right home). CLAUDE.md already says Node 26 — no doc change. Verify `npm ci`/`build` still pass.

## TODO reframe (agreed, part of this pass)

Edit `private/TODO.md` `## M5-prep` so items 6 (nightly Hangfire sweep) and 7 (click-budget
release-gate) read as **operator/deployment-gated**, with the local-buildable boundary noted
(Hangfire job buildable but alerting needs deployment; budget panel needs a queryable store /
deployed App Insights; the Playwright budget assertions are the current local enforcement). Mirror
M0's ⏳ operator-gated phrasing. Check off the five completed items as they land.

## Out of scope

- Nightly Hangfire trust-equation sweep (deployment-gated; `check-invariants` CLI already exists).
- Click-budget dashboard panel + CI release-gate (deployment-gated; `trackInteraction` + Playwright
  budget assertions already exist).
- Any M5 statement-engine work; any new schema/migrations; `RefundIssued` bank-derivation (Phase 3);
  OFX/QFX import (P66); the reporting read-layer ADR (now an explicit M5.1 task).

## Cross-cutting / DoD

- **Zero migrations expected** (all columns exist). If any read needs a new index, it goes through the
  migrations RLS/index helpers and the schema guard.
- Definition of Done (TODO.md): tests at the right altitude (Accounting/Directory unit + integration,
  web component tests, schema drift gate); audit/telemetry unaffected (no new money path);
  empty/loading/error + keyboard path + AA/icon-label on the new Settings toggle; demoable on the seed
  org with no manual fixes.
- Integration gate before close: `reset-db` → migrate-from-blank (unchanged count) → `seed ×2`
  idempotent → `check-invariants --org demo` exit 0 → full `dotnet test` + web lint/typecheck/test/build
  + `format:check` + e2e + `docker build`.

## Sequencing (for the plan)

1. `GetBankBalances.UnclearedCount` (shared by items 1 & 3) → dashboard wiring + tests.
2. Bank-deactivation: port + adapter + command + endpoint + picker filter + Settings UI + tests.
3. `is_system` funnel + behavioral test.
4. Subagents (independent; any time).
5. Node pin (independent; trivial).
6. TODO reframe + check-offs; final integration gate.
