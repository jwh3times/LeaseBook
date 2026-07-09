# Milestone 3 — Tenant Ledger Action Hub: Retrospective

> **Status:** COMPLETE on branch `m3/ledger-hub` (all 7 WPs + the §D Integration Gate). The PR
> `m3/ledger-hub → main` and its merge are the only remaining step (operator, per branch protection).
> **Plan:** `private/planning/m3_plan.md` · **Constraints:** `CLAUDE.md` · **Scope:** PRD §M3.

## What M3 delivered

M3 turned the read-only M2 tenant page into the **action hub** the product is built around — the place
money first moves through the UI. It added the **command + endpoint surface** that wraps the M1 engine,
the **actor attribution** that finally lights up `created_by`/`actor_user_id`, and the **screen** that
makes it usable. It shipped **zero schema migrations** (M3 is schema-stable, P57) and added **no new
journal write path** — every post routes through the existing `IAccountingEvents`/`IReversalService`.

Per work package (all merged inline onto `m3/ledger-hub`):

- **WP-01 — Posting surface.** Eight CQRS commands (`RecordPayment`, `AddCharge`, `IssueCredit`,
  `CollectDeposit`, `CollectPrepayment`, `ApplyDeposit`, `ApplyPrepayment`, `VoidEntry`) wrapping the M1
  events, their POST endpoints (`RequirePMStaff`), the focused ledger CSV (CsvHelper, P55), the
  `ITenantPostingDimensions` consumer port + host adapter that resolves owner/property/unit from the
  active lease (ADR-007/P58), and the deposit/prepayment **receivable guard** (`InsufficientReceivableException`,
  ADR-011).
- **WP-02 — Actor attribution + audit trail.** `IActorContext` (SharedKernel) populated by the
  middleware from the user-id claim; `AppDbContext` + `PostingService` stamp `actor_user_id`/`created_by`
  from it (optional ctor deps → seeder/jobs/harness keep writing null/system); the host `EntryAuditReader`
  - `GET /entries/{id}/audit` resolving actors via an **org-filtered** `asp_net_users` lookup (M3-E6).
- **WP-03 — Backend close-out.** HTTP scenario tests (the `AccountingExceptionHandler`'s first HTTP
  producer: 200/400/409 over the wire), the regenerated `web/src/api/schema.d.ts`, `docs/accounting.md`
  updates, and proof the demo org is byte-stable.
- **WP-04 — Ledger page shell.** `LedgerPage` (header + composer slot + ledger card), `LedgerTable`
  (virtualized via `@tanstack/react-virtual`, keyboard grid nav, flash, voided/reversal styling, the
  `rowActions` seam), `useTenantLedger`/`downloadLedgerCsv`, `ledger.css`. Router → LedgerPage;
  `TenantDetailPage` retired.
- **WP-05 — Inline composer.** Record-payment / Add-charge in place, autofocus amount, Enter posts /
  Escape cancels, defaults that keep a bare payment at **2 interactions** (`trackInteraction`), a
  per-open `sourceRef` idempotency key (P54), and the palette "Record payment" → `?compose=payment` wiring.
- **WP-06 — Apply / void / audit.** `ApplyModal` (deposit/prepayment, target, banks-by-purpose; the P51
  guards warn in place and keep the modal open), `VoidDialog` (reason → linked reversal; `already_reversed`
  friendly), `AuditDrawer` (who/when/what newest-first).
- **WP-07 — DoD + e2e.** Two Playwright specs green against the seeded host (payment ≤3 + telemetry +
  void + audit; over-apply warn); DoD sweep (icon+label badges, `<Money>` org pref, all states, full
  keyboard path).

## Integration Gate evidence (§D)

- `reset-db` → migrate from blank: the **five M0/M1/M2 migrations only** — `dotnet ef migrations list`
  unchanged, **M3 added none** (P57). ✅
- `seed --org demo` ×2 idempotent; `check-invariants --org demo` → **exit 0, all clean**. ✅
- `dotnet build` 0/0; full `dotnet test` green — **143 backend tests** (SharedKernel 26, Architecture 6,
  Accounting 57, Integration 54). ✅
- Web `lint`/`typecheck`/`test`/`build` green — **67 tests** (regenerated `schema.d.ts` compiles). ✅
- `npm run e2e` (host + seed) → M3 budgeted flows green; M2 budgeted-flow + smoke specs still green. ✅
- `docker build` → container serves `/api/health` (200) + the SPA (200). ✅
- `dotnet format --verify-no-changes` clean; gitleaks is a CI-only gate (no local install; repo
  secret-free by construction). ✅
- ADR-010 (write-command surface + actor attribution) + ADR-011 (over-application policy) recorded;
  `docs/accounting.md` updated; `CLAUDE.md` Commands unchanged (M3 added none). ✅

**Total: 210 automated tests (143 backend + 67 web) + 2 e2e specs.**

## Deviations from the plan

1. **Execution model — inline, not subagents.** The plan was authored for orchestrator + per-WP
   subagents; by the operator's choice it was executed **inline, sequentially**, on a single integration
   branch `m3/ledger-hub` (one logical commit per WP) rather than per-WP branches. The WP boundaries,
   contracts (§C), and pins (P50–P59) were honored as written.
2. **Apply entry point moved from the row to the header (WP-06).** §C.4 listed "Apply…" among the
   row-actions, but `DepositCollected` posts to the deposit-liability account and **does not appear in
   the rent ledger** (which projects receivable/prepayment lines) — so there is no deposit row to hang
   the action on. The "Apply held funds" affordance lives on the header "Deposit held" card; the apply
   modal's source toggle covers deposit vs prepayment. Row actions are Void + History.
3. **`IReversalService` gained a non-breaking `sourceRef` overload (WP-01).** To honor P54 for voids
   without churning the 6 existing positional callers (and stay CA1068-clean), `ReverseAsync` got a
   5-arg overload; the keyless 4-arg delegates to it. Engine schema/FKs untouched (P57).
4. **Two pre-existing engine tests updated (WP-01).** `PostingTemplatesTests` and
   `CatalogBalancePropertyTests` applied against charges with **no** prior receivable purely to exercise
   line structure — a precondition ADR-011 now forbids. They post a rent charge first; the asserted line
   sets are unchanged.

## Known limitations (carried, not regressions)

- The e2e leaves net-zero payment+reversal pairs on the demo org (voided back to baseline — balances and
  invariants stay correct; the gate's `reset-db` clears the rows). Assertions are scoped by a per-run
  unique amount so re-runs don't collide.
- `period_closed` (409) is wired through the whole stack but **unreachable** in M3 — nothing closes a
  period until M4 finalize exists (P57).
- gitleaks runs only in CI locally (no local binary); the repo carries no secrets.

## What M4 planning must absorb

1. **The deferred org-aware composite `(org_id, id)` FK + `AccountingTestHarness` rework** (M2 retro
   known-limitation #1; ADR-008 revisit trigger). It is tracked on the **M4.1 TODO line** and is the
   reason M3 stayed schema-stable. M4 is the first milestone after M2 to reopen the journal schema /
   harness — land it there and re-run the gate.
2. **The period-lock surface the composer posts into.** M3 posts only into open periods; once M4
   reconciliation finalize **closes** a period, `period_closed` (409) becomes reachable. The composer
   and apply/void flows must then **surface it** (an inline "this period is reconciled — post into the
   open month" message) the same way they surface `insufficient_receivable` today. The handler mapping
   and the unreachable wiring already exist.
3. **The bank-account deactivation gap (M2-E9).** M3's bank pickers list all active banks; there is
   still no deactivation path, so a bank with journal history can't be retired. M4's register work is
   the natural home.
4. **`RefundIssued` trusts the caller's `BankAccountId` (M1 finding).** Out of M3's tenant-ledger scope
   (no refund surface shipped); the deposit-disposition wizard that derives the bank from the
   liability's source is **Phase 3**. Still owed there.
5. **Telemetry dashboard wiring.** M3 proves `trackInteraction('record-payment', n, met)` fires; the
   dashboard panel + release-gate that *consume* the `ux.budget` events remain a later cross-cutting task.
