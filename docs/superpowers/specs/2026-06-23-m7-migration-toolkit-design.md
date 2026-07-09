# M7 — Migration Toolkit & Import-First Onboarding: Design Spec

> **Status:** Approved design, pre-implementation. **Milestone:** M7 (`private/TODO.md` §M7).
> **Scope authority:** PRD §M7 / Report §5.1 + §4.8
> (import-first onboarding). **Predecessor:** M6 (Bulk Operations) — see `private/planning/m6_retro.md`.
> **Date:** 2026-06-23.

## 1. Summary

M7 turns the empty `LeaseBook.Migrator` shell (`MigratorPlaceholder.cs`) into the toolkit that gets a
real PM off AppFolio and onto LeaseBook **without re-keying** and **without fake history**. The target
is a representative small NC PM coming off AppFolio (~150 units is the design scale). Migration is the
single largest adoption risk (Report §5.1) — if the first month's owner balances don't tie to
the cent, the customer never trusts the platform.

M7 delivers a **balance-forward cutover**: historical ledgers stay in AppFolio; LeaseBook starts clean
but **verified**. At a clean month-end boundary the PM exports AppFolio's closing positions, imports
them as **opening journal entries**, and a **verification gate** proves — line-by-line — that the
imported totals tie to AppFolio before go-live is permitted.

The whole engine + UX is built now. The **one** piece that waits on real data is the concrete AppFolio
column maps: the research spike that catalogs real AppFolio export formats is a 🚧 GATE
that feeds the parser's column-mapping profiles. M7 ships the tolerant parser **seam** + a documented
default profile so plugging in the real columns is configuration, not a redesign.

**M7 exit criteria (adjusted for the data gate):** a synthetic-but-representative cutover fixture
imports to a **$0.00 verification**, the PM signs off, and the org lands on a working dashboard —
entirely through the UI, no SQL. The real-AppFolio run on a staging org is the M8/operator step that
the research spike unblocks.

## 2. Decisions locked in brainstorming

| #   | Decision                          | Choice                                                                                                                                  |
| --- | --------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------- |
| D1  | AppFolio export data availability | **Real export samples not yet in hand.** Build the full architecture + a tolerant/configurable parser seam; defer column maps |
| D2  | Onboarding + verification ambition | **Full guided wizard + hard, recorded sign-off gate** (Report §4.8/§5.1 P0)                                                            |
| D3  | Opening-balance posting model     | **Approach A** — extend `IBalanceForward` (`PostOpeningPositionAsync`) + per-org **Migration Clearing** account; clearing nets to $0/basis = structural tie-out. Existing batched seam + demo golden untouched |
| D4  | Cutover timing                    | **Clean month-end boundary**, balance-forward only (no historical ledger import — history stays in AppFolio)                            |
| D5  | Per-basis figures                 | Import AppFolio's **per-basis** closing figures; tag lines `cash`/`accrual`/`both` so each basis ties independently (never derive one)  |
| D6  | Verification counterparty         | Operator **enters/imports AppFolio's stated closing figures**; the report compares them to imported subledger totals                    |
| D7  | Migrator placement                | **Pure parse/map/validate library** (SharedKernel only); host orchestrates; Accounting posts via an `ISender` port (M5/M6 pattern)      |

## 3. Architecture & module placement

Follows the established M5 (read-side `IOwnerStatementData`) / M6 (write-side `IBatchPosting`) patterns —
no new architectural shape.

- **`LeaseBook.Migrator`** (references `SharedKernel` only): a **pure** parse → map → validate library.
  Input: raw CSV bytes + a named column-mapping profile. Output: typed, validated import rows + a
  row-level error list. No DB, no posting, no HTTP — fully unit-testable in isolation. This is the
  toolkit's testable core and the only place that knows CSV shapes.
- **Host import endpoints** are the composition root that orchestrates the flow: parse → stage
  (`import_batches`/`import_rows`) → post opening entries → compute verification → record sign-off.
  Posting and the verification read cross into Accounting **only** through Migration-owned ports + thin
  host adapters dispatching via `ISender` on the ambient RLS transaction (ADR-007). No cross-module SQL.
- **Accounting** owns the new `OpeningBalanceImported` posting template and the verification read
  (clearing-account balance + imported subledger totals), exactly as it owns statement math in M5.
- **SPA** gets the import-first onboarding wizard that replaces the empty dashboard.

```
CSV bytes + mapping profile
        │  Migrator library (pure): parse → map → validate
        ▼
import_rows (staged, per-row status + errors)   ── operator fixes/remaps, re-parses ──┐
        │  host: post valid rows                                                       │
        ▼  one ambient RLS transaction per batch                                       │
PostOpeningPositionAsync (per row) ─► Accounting posts opening entries vs MigrationClearing │
        │                                                                               │
        ▼  host: verification read                                                      │
Verification report (imported totals vs AppFolio closing figures; clearing residual) ◄──┘
        │  PM reviews; variance must be $0.00
        ▼  PM approves  ──►  immutable snapshot + audit_events  ──►  go-live unblocked
```

**Rejected alternatives:** a standalone CLI-only importer (loses the Report §4.8 import-first
onboarding UX, which is the P0); posting logic inside the Migrator library (drags Accounting concerns
into a SharedKernel-only project, breaks the boundary); a non-journal opening-balance snapshot table
(violates "ledgers are the single source of truth; every balance is a projection of the journal").

## 4. Data model

### 4.1 New tables (org-scoped)

All three go through the migrations RLS helper (`EnableOrgRls`: `org_id` column + `USING`/`WITH CHECK`
policy + FORCE) and are covered by `SchemaGuardTests`. Migration: `AddImportToolkit`.

**`import_batches`** — one row per uploaded file/import run.

- `id` (uuid, pk), `org_id` (uuid, RLS)
- `entity_kind` (text: `properties` | `units` | `owners` | `tenants_leases` | `owner_balances` | `deposit_liabilities` | `bank_balances` | `tenant_receivables`)
- `mapping_profile` (text — named column-mapping profile used)
- `source_filename` (text), `row_count` (int), `error_count` (int)
- `status` (text: `staged` | `posted` | `superseded`)
- `actor` (text), `created_at` (timestamptz)

**`import_rows`** — one row per CSV line (the staging + audit grain).

- `id` (uuid, pk), `org_id` (uuid, RLS), `batch_id` (uuid, fk → import_batches)
- `row_number` (int), `raw_json` (jsonb — original parsed cells), `mapped_json` (jsonb — canonical fields)
- `row_status` (text: `valid` | `error`), `errors_json` (jsonb — [{field, reason}])
- `resulting_journal_entry_id` (uuid, nullable — set at post time for balance rows; append-only, never updated)

**`migration_verifications`** — immutable verification snapshot + sign-off.

- `id` (uuid, pk), `org_id` (uuid, RLS), `cutover_date` (date)
- `expected_json` (jsonb — AppFolio closing figures the operator supplied)
- `actual_json` (jsonb — imported subledger totals + per-basis MigrationClearing residual)
- `variance_total` (numeric(14,2)), `is_tied` (bool — variance == 0 both bases)
- `signed_off_by` (text, nullable), `signed_off_at` (timestamptz, nullable)
- `report_snapshot` (jsonb — frozen line-by-line ✓/✗ report), `created_at` (timestamptz)

The verification snapshot is **immutable once signed** (same pattern as the M4 reconciliation report and
the M5 statement-delivery artifact). Re-verification before sign-off writes a new row; the history is
intentionally auditable.

### 4.2 Chart-of-accounts addition

`MigrationClearing` — a new account added to the CoA template (`ChartOfAccounts.ProvisionAsync`) under a
new **`AccountClass.MigrationClearing`** (the enum has six classes today; the `account_class` DB CHECK on
`accounts` and the denormalized `journal_lines.account_class` column both gain the new value via the
`AddImportToolkit` migration). The new class is what keeps the clearing account out of owner reads —
owner-statement queries select `account_class = owner_equity`, so a different class is **structurally
unreachable** (the same data-model isolation that hides `pm_income`); it is *not* an `is_system` flag
(that concept lives on directory rows, behind `NotSystem()`, not on accounts). It is also outside the
trust equation (computed from `trust_bank`/`owner_equity`/`deposit_liability`/`pm_income`). It is a
transient cutover account: in a tied, post-go-live org its balance is **$0.00 in both bases** — asserted
as a standing invariant (§9).

### 4.3 Consumed, not added

The directory entities (`Owner`, `Property`, `Unit`, `Tenant`, `LeaseLite`) and trust-bank-account
setup already exist from M2 — entity import **creates** these via the existing Directory write paths;
the wizard's "set up trust bank accounts" step reuses the M2 Settings surface. The journal, posting
engine, periods, and the trust-equation invariant come from M1.

## 5. The opening-balance posting model (ADR-020)

> **Reconciliation with the existing `IBalanceForward` seam (verified against code, not assumed).**
> An `IBalanceForward` contract already exists (`Modules.Accounting.Contracts`), explicitly the
> designated "seed/import" seam, with `BalanceForwardRequest`/`BalanceForwardLine`. Today it posts a
> caller-pre-balanced line set as **one** entry, **all basis `both`, with no clearing account** — and
> the **demo seed depends on it** with hand-tied figures the golden tests lock (`DemoJournalSeed`).
> That batched, pre-tied form works for a hand-authored golden but cannot represent a real AppFolio
> import whose figures may *not* tie. M7 therefore **extends `IBalanceForward`** with a new
> per-position, clearing-account, per-basis, idempotent method (`PostOpeningPositionAsync`) and
> **leaves the existing batched method (and the demo seed's golden) untouched** — the demo org models
> an already-migrated org; the new path is exercised by the M7 synthetic cutover fixture. `MigrationClearing`
> is what turns "doesn't balance" into a quantified, dimensioned residual instead of a hard post failure.

The core M7 accounting decision (Approach A, approved). Each imported balance row becomes **one
self-balancing opening journal entry** dated at the cutover boundary, posted via the new
`IBalanceForward.PostOpeningPositionAsync` path, with one leg on the real account and the contra leg on
`MigrationClearing`:

| Imported position           | Real leg                  | Contra leg              | Dimension       | Basis tag      |
| --------------------------- | ------------------------- | ----------------------- | --------------- | -------------- |
| Trust bank book balance     | `DR TrustBank`            | `CR MigrationClearing`  | bank account    | `both`         |
| Owner equity (cash)         | `CR OwnerEquity`          | `DR MigrationClearing`  | owner+property  | `both`         |
| Owner equity (accrued delta)| `CR OwnerEquity`          | `DR MigrationClearing`  | owner+property  | `accrual`      |
| Deposit liability           | `CR DepositLiability`     | `DR MigrationClearing`  | tenant          | `both`         |
| Tenant receivable           | `DR TenantReceivable`     | `CR MigrationClearing`  | tenant/lease    | `accrual`      |
| Held PM fees (if any)       | `CR PMIncome`             | `DR MigrationClearing`  | (no owner dim)  | `both`         |

**Why clearing is the tie-out.** After all rows post, the `MigrationClearing` balance per basis equals
`Σ(debit-side imports) − Σ(credit-side imports)`. Algebraically that is exactly the trust equation in
the cash basis (`bank = owner equity + deposits + held fees`) and the trust equation plus the
receivable/accrued-income identity in the accrual basis. A correct, internally-consistent import drives
clearing to **$0.00 in both bases**; any residual is a quantified, dimensioned discrepancy that the
verification report surfaces and the sign-off gate blocks on. The exact per-basis line construction
(cash equity tagged `both`, the accrued-income delta + receivables tagged `accrual`) lives in ADR-020
and is proven by the property test in §9.

**Idempotency.** Every opening line carries a deterministic `source_ref`:
`opening:{cutover}:{type}={subledgerId}` (e.g. `opening:2026-06-30:owner-equity=<ownerId>:<propertyId>`).
Re-importing the same batch is a no-op via the existing `(org_id, source_ref)` partial unique index +
`DuplicateSourceRefException` (the M6 mechanism — **no new index**). The `source_ref` is **figure-blind**
(it identifies the subledger position, not the amount), so a re-import with a **changed figure does NOT
overwrite** the already-posted opening entry — the duplicate `source_ref` is caught and the row records
as already-posted; the corrected figure never posts. Correcting an already-posted opening figure **before
sign-off** is done by re-provisioning the cutover org and re-importing. An in-product
supersede/correction workflow (and the `import_batch` `superseded` status) is **deferred to M8** — it is
defined in the schema but not exercised by an M7 supersede path.

> **Scope (M7):** only owner / deposit / bank / receivable balance kinds are imported. The **held-PM-fees
> opening position** (ADR-020 §5 table) is **not** imported — held fees would touch `pm_income`, which M7
> keeps out of the import path; any held-fee opening surfaces as a migration-clearing residual the
> operator reconciles before sign-off.

**Atomicity.** One import-balances request = one ambient RLS transaction: stage rows + post all valid
opening entries + write the batch row, commit together or roll back together.

**Period & dating.** Opening entries are dated at the cutover date (the prior month-end close). The
first real LeaseBook month is the open period; M6's rent run charges it. No opening entry posts into a
reconcile-locked period (the existing `IPostingLock` backstop applies).

## 6. Parser seam (tolerant, configurable — AppFolio maps deferred)

- **`IImportParser` per entity kind** (properties/units, owners, tenants/leases, owner balances,
  deposit liabilities, bank balances, tenant receivables). Each consumes a **named column-mapping
  profile** — `AppFolio column header → canonical field` — which is **data**, reusing M4's saved-mapping
  concept. The seam ships a documented `appfolio-default` profile (best-known column names); the
  research spike refines it against real exports; the wizard lets the operator remap unrecognized
  columns inline. **Plugging in the real columns is configuration, not code.**
- **Tolerant ingestion (CsvHelper — already a dependency):** collect-and-continue. A malformed/missing
  field produces an `import_row` with `row_status = error` + `[{field, reason}]`; the batch keeps going.
  One bad row never sinks the import. Errors are surfaced inline in the wizard for fix-and-re-parse.
- **🚧 GATE — `docs/migration/appfolio.md`:** the research spike catalogs which reports/exports the beta
  customer can pull and their real formats. It is the only M7 task blocked on external data; the seam
  above is built independently so the spike fills in profiles, not architecture.

## 7. Verification report + sign-off gate

- **Counterparty figures:** the operator enters (or imports via the same parser) **AppFolio's stated
  closing figures** — owner balances, total deposit liability, trust bank book balances. These are the
  source of truth the import must tie to.
- **The report:** compares AppFolio's figures to the imported subledger totals (read from the journal
  via the Accounting verification read) **line-by-line — ✓/✗** — and shows the per-basis
  `MigrationClearing` residual. A discrepancy names the owner/tenant/account and the dollar gap.
- **Hard sign-off gate:** go-live is **blocked** until `variance == $0.00` (both bases) **and** the PM
  clicks **Approve**. Approval freezes the `migration_verifications` snapshot and writes an
  `audit_events` row (who, when, the figures). This is the "zero manual adjustments" acceptance
  criterion made structural for cutover — the migration analogue of the M5 statement tie-out and the M4
  reconcile-to-$0 finalize.

## 8. Import-first onboarding wizard (Report §4.8)

An org with no journal data lands on a guided, **resumable** checklist instead of an empty dashboard.
Wizard state is **derived**, not a separate flag soup: from `import_batches` (what's imported), whether
opening entries exist, and the `migration_verifications` sign-off status.

1. **Set up trust bank accounts** — reuses the M2 Settings surface. ("Connect banks" via Plaid is
   Phase 2; M7 is manual trust-account setup.)
2. **Import entities** — properties/units → owners → tenants/leases (creates Directory records via
   existing write paths). Row-level errors shown inline.
3. **Import opening balances** — owner equity, deposit liabilities, tenant receivables, bank book
   balances → posts `OpeningBalanceImported` entries (§5).
4. **Verify & sign off** — the §7 gate.
5. **Reconcile first month** — hands off to the M4 register.

Each step shows status/progress and respects the click-budget + keyboard + empty/loading/error
conventions. The wizard is reachable for an in-progress org and disappears once go-live is signed off.

## 9. Cross-module ports (ADR-007 surface) & invariants

The import orchestration lives in the **host** (composition root), so it can inject already-published
Accounting contracts directly — no new write-port/adapter is needed (the host is not subject to the
Operations-style "can't reference Accounting types" constraint; cf. `DemoJournalSeed` injecting
`IBalanceForward`):

- **Write:** the host import service injects the **published `IBalanceForward`** contract directly and
  calls `PostOpeningPositionAsync` per valid balance row (per-row `source_ref`, per-basis tag). No
  `BatchPostingAdapter`-style port is required.
- **Read:** `IMigrationVerificationData` (a thin Accounting read dispatched via `ISender`) returns
  imported subledger totals + the per-basis `MigrationClearing` balance for the verification report.
- Entity creation reuses existing Directory command paths through the host.
- The `Migrator` library stays SharedKernel-only (pure parse/validate); it never references Accounting.

**Invariants (extend the M1.4 harness):**

- The **trust equation holds immediately after import** (existing invariant, now exercised on a
  freshly-imported org).
- **`MigrationClearing == $0.00` per basis** for any org past go-live — a new standing invariant
  (checked by `check-invariants`, asserted in the property test).
- **No `PMIncome` line reachable by owner reads** — the held-PM-fees opening line carries no owner
  dimension (existing structural guarantee, re-asserted over imported data).

## 10. New ADRs

- **ADR-020 — Balance-forward opening-balance posting model.** Extending `IBalanceForward` with
  `PostOpeningPositionAsync`, the new `AccountClass.MigrationClearing`, the per-basis line construction +
  basis tagging, and the clearing-nets-to-$0 tie-out property — and why the existing batched
  `IBalanceForward` method + the demo golden are left untouched. The trust-accounting heart of M7.
- **ADR-021 — Migration toolkit architecture & verification gate.** The Migrator parse/validate library
  + host orchestration, the tolerant parser seam + column-mapping profiles (and why concrete AppFolio
  maps are deferred to the research spike), the staging tables, and the immutable verification sign-off
  gate.

## 11. Testing strategy

- **Migrator library (pure unit):** tolerant ingestion, row-level error reporting, mapping-profile
  application, malformed/missing fields, against golden CSV fixtures (synthetic now; real AppFolio
  fixtures added when the spike lands).
- **Accounting invariant / property-based:** random valid opening-balance sets → `MigrationClearing`
  nets to $0 in **both** bases and the trust equation holds; re-import is idempotent (`source_ref`); no
  `PMIncome` reachable by owner reads. A deliberately **non-tying** set → non-zero clearing surfaced as
  variance (the failure path must be detectable, not self-canceling).
- **Golden-file:** a synthetic "AppFolio cutover" fixture org → import → opening owner/deposit/bank
  positions reconcile to the cent; verification reports $0.00 and is signable.
- **Integration (Testcontainers):** import endpoints, RLS on the three new tables, idempotent
  re-import + supersede, the sign-off gate blocking go-live on non-zero variance (HTTP 409, no
  audit/sign-off row written), variance computation correctness.
- **E2E (Playwright):** the import-first onboarding happy path (entities → balances → verify $0 → sign
  off → working dashboard) **plus** a deliberately non-tying import that shows ✗ and blocks sign-off.

## 12. Definition of Done (per `private/TODO.md`)

- Append-only: opening entries are linked journal entries; no row updated/deleted; `import_rows`/
  `migration_verifications` are write-then-freeze.
- Org scoping: RLS on all three new tables via `EnableOrgRls` + `SchemaGuardTests`; ports run on the
  ambient RLS transaction.
- ADR-007 boundary verified by `ModuleBoundaryTests` (`LeaseBook.Migrator` references `SharedKernel`
  only; cross-module work goes through ports).
- Audit/telemetry on money paths: sign-off + opening-balance posting write `audit_events`; import +
  verification telemetry events fire.
- Empty/loading/error states + keyboard path across the wizard; row-level import errors surfaced.
- Demoable on a fresh org via the synthetic cutover fixture, entirely through the UI, no SQL.

## 13. Work-package sequence (draft — finalized in the implementation plan)

| WP   | Title                                       | Key outputs                                                                                          | ADR     |
| ---- | ------------------------------------------- | ---------------------------------------------------------------------------------------------------- | ------- |
| WP-1 | Migrator library + parser seam              | `IImportParser` per entity kind, tolerant CsvHelper ingestion, mapping profiles, `appfolio-default`, row-level errors, pure unit tests | ADR-021 |
| WP-2 | Opening-balance posting model               | `AccountClass.MigrationClearing` + CHECK update, `IBalanceForward.PostOpeningPositionAsync` (per-basis, clearing contra, idempotent), invariants; demo golden untouched | ADR-020 |
| WP-3 | Staging tables + import endpoints           | `import_batches`/`import_rows` (RLS), entity-import + balance-import endpoints, `IOpeningBalancePosting` port + host adapter, atomicity | —       |
| WP-4 | Verification + sign-off gate                | `migration_verifications` (RLS), `IMigrationVerificationData` read, line-by-line report, hard sign-off gate + immutable snapshot + audit | —       |
| WP-5 | Import-first onboarding wizard (SPA)        | resumable 5-step wizard, inline row-error UI, verify/sign-off screen, empty-dashboard takeover, OpenAPI regen | —       |
| WP-6 | Research spike + parallel-run checklist     | `docs/migration/appfolio.md` (🚧 GATE — real export catalog), `docs/migration/parallel-run.md` + in-app reference | —       |
| WP-7 | DoD close-out                               | synthetic cutover fixture, onboarding e2e (happy + non-tying), `docs/accounting.md` migration section, §D gate, M7 retro | —       |

> WP-1/WP-2 are independent and can proceed in parallel; WP-3 depends on both; WP-6's spike is the only
> externally-gated item and does not block WP-1–WP-5 (the seam + default profile carry the build).

## 14. Out of scope (rejected on sight through Phase 5)

Historical ledger/transaction import (balance-forward only — history stays in AppFolio); Plaid bank
connection (Phase 2); live data migration from non-AppFolio systems (Buildium/Rentec parsers are
future work — the seam allows them, M7 builds AppFolio only); automated AppFolio API pull (export-file
based — AppFolio has no suitable public API for this); the real beta cutover run on production data
(M8/operator, gated on the research spike). PRD exclusions (HOA, commercial, STR, native mobile, full
GL) remain rejected.
