# Milestone 4 — Banking & Reconciliation: Retrospective

> **Status:** COMPLETE on branch `m4/banking` (all 7 WPs + the §D Integration Gate). The PR
> `m4/banking → main` and its merge are the only remaining step (operator, per branch protection).
> **Plan:** `private/planning/m4_plan.md` · **Constraints:** `CLAUDE.md` · **Scope:** PRD §M4.

## What M4 delivered

M4 gave LeaseBook the **bank side of the trust story** and the engine's **first lock surface**. It turned
the empty `Banking` shell into a working module, added a **register** (a journal projection per bank
account), the **clearance** layer over the immutable journal, a **reconcile-in-place → $0.00 → finalize →
lock** workflow with an **immutable report**, **CSV statement import + auto-match + dedup**, and paid down
the carried **composite `(org_id, id)` FK** debt. Two lock paths are now reachable: the per-org
`period_closed` (M1, wired-but-unreachable through M3) and the new per-account `account_period_locked`.

Per work package (all merged inline onto `m4/banking`, one commit each):

- **WP-01 — Composite `(org_id, id)` dimension FKs + harness rework.** `UNIQUE (org_id, id)` on the five
  directory tables; the five `journal_lines` dimension FKs promoted from single-column to composite
  `(org_id, <dim>_id)` (raw, no EF navs — P26); `AccountingTestHarness` seeds FK targets **per test org**
  (the global-org trick removed). New `CompositeDimensionFkTests` proves a cross-org dimension reference is
  now rejected. **ADR-013.**
- **WP-02 — `bank_line_status` + register read model.** The clearance side table (P62, raw upserts, runtime
  `INSERT/UPDATE` grant), `GetBankRegister` (P69 — own-module SQL filtered to `trust_bank`/`pm_operating_bank`
  lines), and `GetBankBalances` extended with cleared/uncleared.
- **WP-03 — Register endpoints + bank-adjustment templates.** The three new events `BankFeeCharged` /
  `InterestEarned` / `TrustTransfer` (P65, balanced per basis, through the one write path), the
  `RecordBankAdjustment` + `ApplyClearances` commands, and `BankRegisterEndpoints`.
- **WP-04 — Reconciliation engine + lock + report.** `bank_reconciliations` aggregate; finalize requires a
  zero difference → marks lines reconciled + stores an immutable `report_snapshot`; `IReconciliationLock`
  consulted by `PostingService` for every bank line (`AccountPeriodLockedException` → 409); `PMAdmin` unlock
  + reason → audit event. **ADR-014.**
- **WP-05 — CSV import + auto-match + Banking module.** `Modules.Banking` stood up: `IStatementParser` /
  `CsvStatementParser`, the `AutoMatcher` heuristic (P67), dedup hash, four org-scoped tables, and the
  ADR-007 ports `IBankRegister` / `IBankClearing` with host adapters dispatching Accounting via `ISender`
  (Banking never touches `journal_lines`). **ADR-015.**
- **WP-06 — Banking SPA.** `/banking` ported from `screen-bank`: account tabs + balance strip + filterable
  register, reconcile-in-place (start ≤1 click telemetry), the CSV import wizard, reconciliation history
  with report download, and `account_period_locked` surfaced inline in the M3 composer/apply/void via a
  shared `LOCKED_PERIOD_MESSAGE`.
- **WP-07 — DoD + e2e + gate (this WP).** The **P72 demo-clearance seed** (see deviation #2), a
  keyboard-operability fix on the register's reconcile checkbox, two Playwright specs
  (reconcile→$0→finalize→lock; import→match→clear→dedup), the `docs/accounting.md` banking section, the
  §D gate, and this retro.

## Integration Gate evidence (§D)

- `reset-db` → migrate from blank: M0–M3 migrations **+ the four M4 migrations** (`CompositeDimensionFks`,
  `AddBankLineStatus`, `AddBankReconciliations`, `AddStatementImport`) apply cleanly; `migrations list`
  matches; `SchemaGuardTests` green. ✅
- `seed --org demo` ×2 idempotent; `check-invariants --org demo` → **exit 0, all clean**; the register
  reproduces the prototype mix — **Operating Trust book 248,930.14 < cleared 254,300.14, 3 uncleared**;
  deposit trust fully cleared. ✅
- `dotnet build` 0/0; full `dotnet test` green — **170 backend tests** (SharedKernel 26, Architecture 6,
  Accounting 67, Banking 12, Integration 59). ✅
- Web `lint` (0 errors) / `typecheck` / `test` (**75**, 20 files) / `build` green; `schema.d.ts` regenerated
  with **no drift** (M4 added no endpoint surface in WP-07). ✅
- `npm run e2e` (serial, one worker) → **9 specs green**: the two M4 specs plus the M2/M3 budgeted-flow +
  smoke specs. ✅
- `docker build` (full stack) → the container serves `/api/health` (200), the SPA (200), and `/banking`
  (200) on :8082. ✅
- `dotnet format --verify-no-changes --exclude …/Migrations` clean. ✅
- ADR-013 / ADR-014 / ADR-015 recorded; `docs/accounting.md` gained a "Banking: the register, clearing &
  reconciliation" section; `private/TODO.md` §M4 checked. ✅

**Total: 245 automated tests (170 backend + 75 web) + 2 new e2e specs (9 e2e total).**

## Deviations from the plan

1. **Execution model — inline, not subagents.** As in M0–M3, the plan was authored for orchestrator +
   per-WP subagents but executed **inline, sequentially**, one commit per WP on `m4/banking`. WP boundaries,
   §B contracts, and pins P60–P72 were honored as written.
2. **The P72 demo-clearance seed was missing and was implemented in WP-07.** P72 ("seed `bank_line_status`
   so the register reproduces the prototype mix") was a pin but had **no explicit task in any WP-02…06 task
   list**, and the existing golden test asserted only **Book** (which is journal-derived and unaffected by
   clearance) — so the gap stayed green and unnoticed until the §D gate's "register reproduces the mix" step
   exposed `bank_line_status` with **zero rows**. WP-07 added `DemoBankClearingSeed` (raw upsert on the side
   table, mirroring `ApplyClearances`; idempotent; marks every Operating Trust + deposit-trust asset line
   cleared except the three netting −5,370 — the −8,200 disbursement, the +1,380 Pryor and a +1,450 Carter
   deposit) **and** extended `GoldenFileTests` to assert cleared 254,300.14 / 3 uncleared, so the mix is now
   contract-enforced and cannot silently regress.
3. **The e2e suite is now serial (`workers: 1`).** Finalizing a reconciliation is a **persistent
   per-account-month lock** (only a `PMAdmin` unlock releases it), so a parallel worker reconciling the
   current month races any other spec posting into that month on the same account. The config now pins one
   worker; files run in discovery order (budgeted → m3 → m4 → smoke), so the M3 June payment posts+voids
   before the M4 spec locks June.
4. **Register reconcile-checkbox keyboard fix (DoD sweep).** The per-row clearance checkbox was `readOnly`
   with toggling on the row's mouse `onClick`, so a keyboard user could not tick items. It is now an
   interactive checkbox (`onChange` toggles, Space-operable; the row click stops propagation to avoid a
   double-toggle), with a test asserting it is not read-only.
5. **`docs/accounting.md` banking section written in WP-07.** WP-04's edit to the "Closing a period"
   paragraph was thin and conflated the per-org period close with the per-account reconciliation lock; WP-07
   corrected that distinction and added the full register/clearing/reconcile/adjustment/import section.
6. **CI `format:check` (`prettier --check .`) caught 7 unformatted WP-05/06 web files** (`banking.css`,
   `BankingPage.tsx`, `ImportWizard.tsx`/`.test.tsx`, `ReconcileBar.tsx`, `ReconciliationHistory.tsx`,
   `ApplyModal.test.tsx`) after the PR opened — the **per-WP gate (§A.4) lists lint/typecheck/test/build but
   not `format:check`**, so prettier-dirty files passed every local gate (lint is a separate, passing check)
   until CI. Fixed in commit `9f439a3` (`npm run format`, whitespace only). **Process fix for M5: add `npm
   run format:check` to the §A.4 per-WP gate** (next to `dotnet format --verify-no-changes`, which the
   backend gate already has).

## Known limitations (carried, not regressions)

- **Dashboard `uncleared` KPI is still hardcoded to 0.** `DashboardService` returns `Uncleared: 0 /
  UnclearedCount: 0` with a static "Uncleared items appear once the bank register lands (M4)" action item,
  and `DashboardTests` asserts the 0. The live source now exists (`GetBankRegister` / `GetBankBalances`),
  but wiring it was left out of M4 deliberately: it is not an M4 exit criterion, and changing the
  cross-module `DashboardService` would churn `DashboardTests`. **Carried to M5** (see below).
- **PM Operating clearance is not seeded** (its lines stay all-uncleared). The §D gate only requires the
  Operating Trust mix; the prototype's PM-Operating figures (uncleared 1,125.25 / 2 items) cannot be derived
  from the actual journal (it has only two PM-Operating asset lines), so they were not reproduced.
- **The reconcile e2e is single-run-per-seed.** Finalize is irreversible without an admin unlock, so the
  spec is designed to run against a fresh seed; the gate's `reset-db` + `seed` provides exactly that.
- **OFX/QFX import deferred (P66).** CSV-only this milestone; the `IStatementParser` seam is in place so it
  slots in without reworking the importer.
- **Bank-account deactivation (M2-E9)** is still open (carried from M3); the register pickers list all active
  banks. **`RefundIssued` bank-derivation** remains Phase 3.

## What M5 planning must absorb

1. **Wire the dashboard uncleared KPI to the live register.** The hardcoded `Uncleared: 0` /
   `UnclearedCount: 0` and the "once the bank register lands (M4)" action text should now read from
   `GetBankBalances` (sum of per-account uncleared) and the per-account uncleared count; update
   `DashboardTests` with the real seed figures (Operating Trust 3 uncleared). Small, but it is the visible
   M4→M5 loose end.
2. **The reconciliation report snapshot feeds the report catalog.** §M5.3 report #4 ("Bank reconciliation,
   from M4 snapshots") consumes the immutable `report_snapshot` JSON stored at finalize — M5 renders it
   (PDF/print) rather than recomputing it.
3. **The dedicated reporting read-layer (the ADR-007 exception) lands in M5.** The register/balance reads
   live inside Accounting today; the statement engine is the sanctioned cross-schema reader. M5 decides what
   moves there and records its own ADR.
4. **Bank-account deactivation** (still open) — the register/settings surface is its natural home; reconsider
   in M5 if a statement/reporting flow needs to exclude a retired account.
