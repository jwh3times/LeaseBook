# M6 — Bulk Operations: Design Spec

> **Status:** Approved design, pre-implementation. **Milestone:** M6 (`private/TODO.md` §M6).
> **Scope authority:** PRD §M6 / Report §4.2 ("property management is batch-shaped").
> **Predecessor:** M5 (Owner Statements & Reporting) — see `private/planning/m5_retro.md`.
> **Date:** 2026-06-22.

## 1. Summary

M6 turns the scaffolded `Modules.Operations` shell into a working module that delivers the three
batch workflows of a property manager's month: the **monthly rent charge run**, the **late-fee run**,
and the **owner disbursement run** (with management fee folded in). Each follows one shared pipeline —
`preview → confirm → atomic post → run history` — so the fiduciary concerns (idempotency, atomicity,
audit, run-history) are written once and inherited, not copy-pasted per run.

Every posting event M6 needs already exists from M1 (`RentCharged`, `FeeCharged(FeeKind.Late)`,
`ManagementFeeAssessed`, `OwnerDisbursed` with its `Reserve` floor). **M6 is workflow over existing
posting primitives** — it adds preview computation, proration/fee math, a late-fee policy model, run
history, and the cross-module batch-posting path. No new posting math lives outside the posting
templates.

M6 also clears the M5 carried follow-ups (per the scope decision: _core + all M5 follow-ups_).

**M6 exit criteria (from `private/TODO.md`):** simulate a full month on the demo org — rent run, late
fees, mid-month activity, reconciliation, statements, disbursement run — entirely through the UI, no SQL.

## 2. Decisions locked in brainstorming

| #   | Decision                            | Choice                                                                                                                                                |
| --- | ----------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| D1  | M6 scope envelope                   | Core (3 runs + run history + dashboard CTA) **+ all M5 backend follow-ups**                                                                           |
| D2  | Management-fee assessment placement | **Folded into the disbursement run** (one run posts `ManagementFeeAssessed` + `OwnerDisbursed` atomically per owner)                                  |
| D3  | Management-fee basis                | **Owner equity available at run time** × effective bps (not fee-on-rent-collected)                                                                    |
| D4  | Late-fee policy location            | **Org-level default (`OrgSettings`) + nullable per-lease override (`LeaseLite`)**; resolve `override ?? org default`; NC §42-46 cap clamps the result |
| D5  | Proration                           | **Computed in M6** — actual-days/month, inclusive of move-in day, half-up to cent (ADR-017)                                                           |
| D6  | Run-engine architecture             | **Shared run-engine in `Modules.Operations`, per-run strategies** (Approach 1)                                                                        |
| D7  | Work-package granularity            | **7 WPs** (one per run + engine + web + follow-ups + DoD)                                                                                             |

## 3. Architecture & module placement

The `Operations` module owns the run domain; cross-module reads and writes go through Operations-owned
ports + host adapters, per ADR-007 — exactly mirroring M5's read path (`IOwnerStatementData` +
`OwnerStatementDataAdapter` dispatching via `ISender`).

**Shared run-engine (one pipeline, per-run strategies):**

```
preview(runType, period)  ── compute, do NOT persist ──►  preview DTO (per-target rows + flags + exceptions)
        │
        ▼  PM reviews, (selectively) confirms
confirm(runType, period, selection)
        │  one ambient RLS transaction:
        ├─ Operations writes bulk_runs + bulk_run_items (snapshot at confirm)
        ├─ dispatch IBatchPosting → BatchPostingAdapter loops IAccountingEvents.PostAsync per intent
        └─ commit together (atomic) or roll back together
```

A per-run **strategy** supplies only: (a) the preview projection (which targets, what amounts/flags),
and (b) the event list to post on confirm. Everything else — endpoints, run-history persistence,
idempotency check, audit, atomicity — is shared engine code.

**Rejected alternatives:**

- _Three independent vertical slices_ — duplicates idempotency/atomicity/run-history across three
  money-touching paths; drift risk on exactly the fiduciary code that must not diverge.
- _Runs inside Accounting_ — violates the module map (Operations is M6's module), drags Directory
  lease-reads into the core module, bloats Accounting with workflow/UI.

## 4. Data model

### 4.1 New Operations tables (org-scoped)

Both go through the migrations RLS helper (`EnableOrgRls`: column + `USING`/`WITH CHECK` policy +
FORCE) and are covered by `SchemaGuardTests`.

**`bulk_runs`**

- `id` (uuid, pk), `org_id` (uuid, RLS)
- `run_type` (text/enum: `rent` | `late_fee` | `disbursement`)
- `period_year` (int), `period_month` (int)
- `status` (text: `committed` — only committed runs are persisted)
- `actor` (text — who ran it), `created_at` (timestamptz)
- `summary_json` (jsonb — counts, totals, exception count for the run summary report)

**`bulk_run_items`**

- `id` (uuid, pk), `org_id` (uuid, RLS), `run_id` (uuid, fk → bulk_runs)
- `target_kind` (text: `lease` | `owner`), `target_id` (uuid)
- `snapshot_json` (jsonb — the per-target computed amounts/flags captured at confirm)
- `resulting_journal_entry_id` (uuid, nullable — promoted column alongside `snapshot_json` for
  direct DB querying; null for skipped/excluded items; set at INSERT only — append-only, never updated)
- `item_status` (text: `posted` | `skipped` | `excluded`)

The pre-confirm preview is **computed on demand and not persisted**; only the committed run's snapshot
is stored (it is the auditable object — who ran, preview snapshot, postings).

### 4.2 Late-fee policy config (Directory)

**`OrgSettings` (new columns — org default policy):**

- `RentDueDay` (int, default 1)
- `LateFeeGraceDays` (int, default 5)
- `LateFeeKind` (enum: `Flat` | `Percent`)
- `LateFeeAmount` (Money — used when `Flat`) / `LateFeeRateBps` (int — used when `Percent`)

**`LeaseLite` (new nullable override columns — set at lease signing):**

- `LateFeeGraceDaysOverride?` (int)
- `LateFeeKindOverride?` (enum)
- `LateFeeAmountOverride?` (Money) / `LateFeeRateBpsOverride?` (int)

**Resolution:** effective policy = lease override ?? org default, per field. **NC G.S. §42-46 clamp**
is applied to the _resolved_ fee at compute time: the late fee may not exceed the greater of $15.00 or
5% of the monthly rent (statutory ceiling — a lease may specify _less_, never more). The clamp is a
correctness rule (no ADR needed; it is statutory), enforced in the late-fee compute path and covered by
a behavioral test.

> Per-lease override slightly front-runs Phase-3 lease management. Scope is held to exactly the four
> nullable columns above; LeaseLite is not otherwise expanded in M6.

### 4.3 Already modeled — consumed, not added

- `Owner.ReserveAmount` — disbursement reserve floor (already enforced by `GuardReserveFloorAsync`).
- `Owner.DefaultMgmtFeeBps` + `Property.MgmtFeeBps` — resolved by Directory's `IManagementFeeConfig`
  (`property override ?? owner default`); M6 supplies the _fee math + rounding_ the resolver
  deliberately omits.

## 5. The three runs

### 5.1 Monthly rent charge run

- **Preview:** every active lease for the period. Per row: tenant/unit/property, rent amount,
  proration status, already-charged status. Leases whose `StartDate`/`EndDate` falls mid-period are
  **prorated** (actual-days/month, inclusive of move-in day, half-up to cent — ADR-017).
- **Exceptions list (shown, never silently dropped):** lease with no rent set, lease ended before the
  period, target period locked by a finalized reconciliation.
- **Confirm:** posts `RentCharged` to all chargeable, unguarded leases atomically.
- **Idempotency (same-source):** per `(lease, period)` via `source_ref` (e.g. `rent:2026-06:lease={id}`).
  Re-running picks up only newly-eligible leases (e.g. a lease added mid-month) and shows the rest as
  already-charged; never double-posts.
- **Cross-source period guard (Fix A):** the `IPeriodChargeGuard` port (host-adapted via
  `GetTenantsChargedInPeriod` against `journal_entries` JOIN `journal_lines`) detects any
  `event_type='RentCharged'` entry in the period for the tenant, regardless of `source_ref` value.
  This prevents double-charging tenants whose charges were posted by the M3 manual composer, seed
  data, or CSV import — not just by a prior bulk run. Leases with an existing charge are flagged
  `AlreadyDone` in preview and `Skipped` in confirm.
  _Demo behavior:_ May 2026 — 6 of 7 leases have seed `RentCharged` entries (sourceRef=null);
  only Devon Pryor (T2, $1,380) has no May charge → 1 eligible, 6 AlreadyDone.

### 5.2 Late-fee run

- **Policy resolution:** per lease, override ?? org default, NC-clamped (§4.2).
- **Preview:** delinquent ledgers past the grace day (`RentDueDay + LateFeeGraceDays`) with an
  outstanding balance → computed late fee per the resolved+clamped policy. Reuses the M5
  `GetDelinquencyAging` read for the delinquency signal.
- **Confirm:** **selective** — the PM picks which delinquent ledgers to charge; posts
  `FeeCharged(FeeKind.Late)`. Always reviewable before posting; never silent.
- **Idempotency:** per `(lease, period)` via `source_ref`.

### 5.3 Owner disbursement run (with folded management fee)

- **Preview per owner:** gross owner equity available → **management fee assessed** (= equity at run
  time × effective bps, rounded per ADR-018) → net before reserve → reserve held
  (`Owner.ReserveAmount`) → amount disbursed.
- **Exclusions (stated reason):** owners whose net would fall below the reserve floor, or with
  non-positive equity, are auto-excluded with a reason; the existing `ReserveFloorException` is the
  posting-time backstop.
- **Confirm:** posts, **atomically per owner**, `ManagementFeeAssessed` + `OwnerDisbursed` + a bank
  withdrawal record. The withdrawal in Phase 1 is a check/manual-ACH **reference string** (real
  Stripe ACH is Phase 2). A run summary report is stored (`summary_json`).
- **Idempotency:** per `(owner, period)` via `source_ref` on both the fee and the disbursement legs.

## 6. Cross-module ports (ADR-007 surface)

All ports are **Operations-owned** (declared in `Modules.Operations.Contracts`), implemented by thin
**host adapters** that dispatch via `ISender` on the ambient RLS transaction, returning **batch** maps.
Operations never references another module's entity or event types.

**Reads (preview inputs):**

- `ILeaseScheduleData` — active leases (tenant, unit, property, owner, rent, term) ← Directory.
- `ILateFeePolicyData` — resolved late-fee policy per lease ← Directory.
- `IManagementFeeConfig` — Operations' **own** port (per P49) wrapping Directory's resolver ← Directory.
- `IOwnerEquityBalances` — owner equity available at run time ← Accounting (reuses M5 balance reads).
- `IDelinquencyData` — delinquent ledgers past grace ← Accounting (reuses M5 `GetDelinquencyAging`).
- Bank-account display (name/mask for the withdrawal record) ← Banking/Accounting.

**Writes (posting):**

- _Implementation refinement (ground-truth, WP-7):_ NO new `PostEventBatch` command was introduced.
  The host `BatchPostingAdapter` (implementing `IBatchPosting`) loops the **existing public**
  `IAccountingEvents.PostAsync` for each intent within the ambient RLS transaction — one call per
  target, catching per-item exceptions (`DuplicateSourceRefException`, `AccountPeriodLockedException`,
  `PeriodClosedException`) to record individual item outcomes. This is simpler than a new batch
  command and preserves the existing per-event exception contract.
- Operations dispatches writes through the `IBatchPosting` port + `BatchPostingAdapter` host adapter.
  The port speaks Operations primitives (target id, amount, date, period, kind); **the adapter**
  translates intent into the correct Accounting event types (`RentCharged`, `FeeCharged`,
  `ManagementFeeAssessed`, `OwnerDisbursed`) — Operations stays free of Accounting's event types.
  This is the write-direction analogue of M5's read-direction ADR-016, recorded in ADR-019.

## 7. Idempotency, atomicity & locked periods

- **Atomicity:** one HTTP request = one ambient RLS transaction. Within it, Operations writes its
  `bulk_runs`/`bulk_run_items` rows and the adapter loops `IAccountingEvents.PostAsync` per intent;
  Accounting writes the journal entries; all commit together or roll back together. A batch is all-or-nothing.
- **Idempotency grain:** per `(target, period, run_type)` via a deterministic `source_ref`, **plus** the
  cross-source period guard (Fix A): `IPeriodChargeGuard` detects a `RentCharged`/`FeeCharged(late)` already
  posted for the tenant in the period **by any means** (manual, import, seed, or a prior run), marking the
  lease `AlreadyDone` so the bulk run never double-charges across sources. The preview
  marks each target as already-done vs pending; confirm posts only pending targets.
  _Implementation refinement (ground-truth, WP-7):_ M6 does NOT add a new partial unique index. It
  **reuses the EXISTING `(org_id, source_ref)` partial unique index** on `journal_entries` (introduced
  in an earlier milestone) and the existing `DuplicateSourceRefException` that fires when the index
  rejects a duplicate. M6's contribution is the **source_ref key convention** for bulk runs:
  `{runType}:{year}-{month:00}:{targetKind}={targetId}` (e.g. `rent:2026-05:lease=<leaseId>`,
  `disbursement:2026-05:owner=<ownerId>`). The index is the backstop guaranteeing no double-post
  even under concurrent confirms.
- **Locked periods:** posting into a reconcile-finalized period is rejected by the existing
  `IPostingLock`; the run surfaces it as an exception row, not a crash.
- **Cross-source double-charge guard (Fix A — added after M6 WP-7):** the `source_ref`-based
  idempotency only protects against re-running the SAME bulk-run key. It does not prevent a bulk run
  from double-charging a tenant whose rent was posted by the M3 composer, seed data, or CSV import
  (different key, same economic meaning). The `IPeriodChargeGuard` port (Operations-owned, host
  adapter dispatches Accounting's `GetTenantsChargedInPeriod` via ISender) closes this gap for rent
  and late-fee runs. The guard is checked at both preview time (→ `AlreadyDone` flag) and confirm
  time (→ `Skipped` with reason `already_charged_in_period`). Disbursement has no analogous gap
  (equity is spent by the posting — no double-charge is possible at the DB level).
- **Month/year validation (Fix E):** preview and confirm endpoints validate `month ∈ 1..12` and
  `year ∈ 2000..2100`, returning HTTP 400 `invalid_period` before delegating to the strategy. This
  prevents `DateOnly` from throwing a 500 on out-of-range values.

## 8. M5 carried follow-ups (folded into M6 per D1)

1. **Demo-seed finalized reconciliation** — add a finalized `BankReconciliations` row to
   `DemoBankClearingSeed` so the `bank-rec` report preview shows real data on a fresh org (also required
   for the full-month exit-criteria demo).
2. **`ReconciliationSnapshotRow` += `bankName`/`accountMask`** — statement "reconciles-to" card
   fidelity (backend field + adapter update + client regen).
3. **OpenAPI preview response shape** — annotate `GET /api/reports/{id}/preview` so the generated client
   types it correctly; regen; remove the raw-fetch workaround in `useReportPreview`.
4. **`BuilderPanel` unconditional fetches** — pass an `enabled` flag to
   `useOwners`/`useProperties`/`useBankBalances` so they fetch only when their filter chips are shown
   (or correct the misleading comment).

## 9. New ADRs

- **ADR-017 — Rent proration method.** Actual-days-in-month; daily rate = monthly rent /
  actual-days-in-month; charge = daily rate × days occupied (move-in day inclusive); half-up rounding to
  the cent. Records the convention and the new golden figures it produces.
- **ADR-018 — Management-fee rounding.** Fee = owner equity available at run time × effective bps;
  half-up rounding to the cent; records the basis (equity-at-run-time, not collected-rent) and the
  rounding rule. (This is the rounding ADR pre-assigned by `IManagementFeeConfig`.)
- **ADR-019 — Bulk-run engine & cross-module batch posting.** The Operations run pipeline + the
  `IBatchPosting` port (host `BatchPostingAdapter` loops the existing `IAccountingEvents.PostAsync`; no new
  command). Documents the write-direction cross-module pattern, complementing M5's read-direction ADR-016.

## 10. Testing strategy

- **Invariant / property-based:** the trust equation (bank book balance = Σ owner equity + Σ deposit
  liabilities + held PM fees) holds after every run; re-running any run never double-posts and never
  breaks balance (idempotency property over generated re-run sequences); mgmt-fee + disbursement leave
  the equation intact.
- **Golden-file:** new prorated-rent golden figures on the demo seed (ADR-017); mgmt-fee golden figures
  (ADR-018). Seed numbers change deliberately, with the golden tests, per the "seed is sacred" rule.
- **Integration:** each run's preview + confirm endpoints; locked-period rejection; reserve-floor
  exclusion with reason; selective late-fee posting; NC §42-46 clamp behavior.
- **e2e (Playwright):** the **M6 exit criteria** as a serial spec — a full month on the demo org (rent
  run → late fees → mid-month activity → reconciliation → statements → disbursement run) entirely
  through the UI, no SQL — plus the dashboard "Run owner disbursements" CTA navigating to the run.

## 11. Definition of Done (per `private/TODO.md`)

- Tick-depth budgets respected (start a run ≤ a couple of clicks; dashboard CTA wired).
- Append-only: runs post linked entries only; no journal row updated/deleted.
- Org scoping: RLS on both new tables via `EnableOrgRls` + `SchemaGuardTests`; ports run on the ambient
  RLS transaction.
- ADR-007 boundary verified by `ModuleBoundaryTests` (`Modules.Operations` references `SharedKernel`
  only).
- Audit/telemetry on the money paths: every committed run writes an `audit_events` row; run + posting
  telemetry events fire.
- Empty/loading/error states + keyboard path on the three run screens and the run-history view.
- Demoable on the seed org (the full-month exit-criteria walkthrough).

## 12. Work-package sequence (7 WPs)

| WP   | Title                                          | Key outputs                                                                                                                                              | ADR     |
| ---- | ---------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------- | ------- |
| WP-1 | Operations module + run-engine skeleton        | `bulk_runs`/`bulk_run_items` (RLS), shared preview/confirm pipeline, run-history persistence, `IBatchPosting` port + host `BatchPostingAdapter` (loops `IAccountingEvents.PostAsync`) | ADR-019 |
| WP-2 | Rent charge run + proration                    | rent preview/confirm, proration math, idempotency, new golden figures                                                                                    | ADR-017 |
| WP-3 | Late-fee policy + run                          | `OrgSettings`/`LeaseLite` policy columns, resolver, NC §42-46 clamp, selective late-fee run                                                              | —       |
| WP-4 | Disbursement run + folded mgmt fee             | mgmt-fee math, reserve-floor exclusion, `ManagementFeeAssessed` + `OwnerDisbursed` + bank withdrawal record, run summary                                 | ADR-018 |
| WP-5 | Web: run screens + dashboard CTA + run history | three run screens (preview→confirm), run-history view, wire the "Run owner disbursements" CTA, OpenAPI regen                                             | —       |
| WP-6 | M5 carried follow-ups                          | demo-seed finalized reconciliation, `ReconciliationSnapshotRow` fields, OpenAPI preview shape + remove raw-fetch, `BuilderPanel` `enabled` flag          | —       |
| WP-7 | DoD close-out                                  | full-month e2e, `docs/accounting.md` bulk-ops section, §D integration gate, M6 retro                                                                     | —       |

## 13. Out of scope (rejected on sight through Phase 5)

Auto-proration beyond move-in/out (mid-period rent changes), recurring-charge schedules beyond rent,
per-property late-fee policy resolution (org default + per-lease override only), live Stripe ACH
disbursement (Phase 2), full lease management (Phase 3). PRD exclusions (HOA, commercial, STR, native
mobile, full GL) remain rejected.
