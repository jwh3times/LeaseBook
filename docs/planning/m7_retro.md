# Milestone 7 — Migration Toolkit & Import-First Onboarding: Retrospective

> **Status:** COMPLETE on branch `m7/migration-toolkit`. §D integration gate PASSED (controller-run
> 2026-06-23 — all green; see §D section). The PR `m7/migration-toolkit → main` is the remaining step
> (operator, per branch protection).
> **Plan:** `private/planning/m7_plan.md` (brief at `m7-wp7-brief.md`) ·
> **Design spec:** `docs/superpowers/specs/2026-06-23-m7-migration-toolkit-design.md` ·
> **Constraints:** `CLAUDE.md` · **Scope:** PRD §M7.

---

## What M7 delivered

M7 turns the empty `LeaseBook.Migrator` placeholder into a balance-forward AppFolio migration
toolkit: a tolerant CSV parse/validate library, opening-balance posting against a new
`MigrationClearing` account (clearing nets to $0/basis = the tie-out), a hard verification
sign-off gate, and an import-first onboarding wizard. A PM can now cut over from AppFolio with
imported totals **proven correct before go-live** — entirely through the UI, no SQL.

Per work package:

### WP-1 — Migrator library + parser seam

`src/LeaseBook.Migrator/` ships as a pure, `SharedKernel`-only library. Core exports:

- `CsvImporter.Read<TRow>(Stream, ColumnMappingProfile, Func<RowContext, TRow?>)` — tolerant
  collect-and-continue ingestion; one bad row never sinks the batch.
- `RowContext` — per-row context carrying canonical cells and a `Reject<T>(field, reason)` helper;
  no static state, no exceptions for expected parse mismatches.
- `ColumnMappingProfile` — named profile: canonical field → candidate header strings + required
  flag. Missing required columns produce top-level errors; optional missing columns degrade
  gracefully to empty strings.
- `AppFolioProfiles.For(EntityKind)` — `appfolio-default` profiles for all eight entity kinds
  (Owners, Properties, Units, TenantsLeases, OwnerBalances, DepositLiabilities, BankBalances,
  TenantReceivables). Header candidates are best-guess strings; real headers are a research-spike
  gate (WP-6 / `docs/migration/appfolio.md`).
- `EntityImporter` — static binders that centralize CSV-to-typed-row mapping per kind.
- Typed rows: `OwnerRow`, `PropertyRow`, `UnitRow`, `TenantLeaseRow`, `OwnerBalanceRow`,
  `DepositLiabilityRow`, `BankBalanceRow`, `TenantReceivableRow`.
- `LeaseBook.Tests.Migrator` project: pure unit tests (no Testcontainers) covering tolerant
  ingestion, row-level error collection, mapping-profile resolution, and malformed fields.

`ModuleBoundaryTests` enforces the `SharedKernel`-only assembly constraint.

### WP-2 — Opening-balance posting model (ADR-020)

- `AccountClass.MigrationClearing` — seventh account class, structurally invisible to owner
  statements and the trust equation (same isolation mechanism as `pm_income`).
- `AccountCodes.MigrationClearing = "migration_clearing"` — the stable code for the singleton
  clearing account provisioned in `ChartOfAccounts.ProvisionAsync`.
- `IBalanceForward.PostOpeningPositionAsync(OpeningPositionRequest, ct)` — posts ONE
  self-balancing entry per imported position: real account leg + equal-opposite `MigrationClearing`
  contra, both tagged `req.Basis`, dated at `req.Cutover`, keyed by `req.SourceRef`.
- `AddImportToolkit` migration updates the `account_class` DB CHECK on `accounts` and the
  denormalized `journal_lines.account_class` column to include `migration_clearing`.
- `InvariantChecks` extended with the clearing-nets-to-$0 standing invariant (both bases),
  asserted on orgs that have completed sign-off.
- The existing `IBalanceForward.PostAsync(BalanceForwardRequest)` and `DemoJournalSeed` golden
  are untouched — byte-identical.

### WP-3 & WP-4 — Staging tables, import endpoints, verification + sign-off gate

`AddImportToolkit` migration also adds three org-scoped tables (all `EnableOrgRls` +
`SchemaGuardTests` covered; balance tables additionally `RevokeAppendOnly`):

- `import_batches` — one row per CSV upload (entity kind, mapping profile, filename, row/error
  counts, status: `staged` / `posted` / `superseded`).
- `import_rows` — one row per CSV data line (`raw_json`, `mapped_json`, `row_status`,
  `errors_json`, `resulting_journal_entry_id`). `mapped_json` doubles as the external-id→LeaseBook-id
  resolution store between entity and balance imports.
- `migration_verifications` — immutable verification snapshots (write-once; sign-off inserts a
  new row rather than updating the original).

Host endpoints under `src/LeaseBook.Web/Onboarding/`:

- `POST /api/onboarding/import/entities` — entity CSV import (creates Directory rows via ISender).
- `POST /api/onboarding/import/balances` — balance CSV import (posts via `IBalanceForward`).
- `POST /api/onboarding/verification` — run verification report; writes a `migration_verifications`
  row; returns `IsTied` + variance lines + clearing residuals.
- `POST /api/onboarding/signoff/{id}` — sign off on a verification; HTTP 409 if `IsTied == false`
  (no side effect on the blocked path); on success, inserts a new signed row + explicit
  `migration-signed-off` audit event.
- `GET /api/onboarding/status` — six derived flags (BanksConfigured, EntitiesImported,
  BalancesImported, Verified, SignedOff, HasJournalData); no dedicated status table.

Import contract: **JSON body** with `csvContent` as a string field (not multipart) — rationale in
ADR-021.

`IMigrationVerificationData` / `GetMigrationVerificationData` — Accounting-owned query handler that
reads clearing residuals + subledger totals from `journal_lines`/`accounts` only (no cross-module
SQL). Dispatched via `ISender` from `VerificationService`.

### WP-5 — Import-first onboarding wizard (SPA)

`web/src/features/onboarding/`:

- `OnboardingPage.tsx` — five-step guided checklist (Set up bank accounts, Import entities, Import
  balances, Verify & sign off, Reconcile first month).
- `OnboardingChecklist.tsx` — derived step-gating from the `/api/onboarding/status` response.
- `ImportStep.tsx` — per-kind CSV upload with inline row-error display.
- `VerificationStep.tsx` — variance report display + sign-off button (disabled when not tied).
- `onboarding.ts` — TanStack Query hooks for all onboarding endpoints.
- `DashboardPage.tsx` — redirects to `/onboarding` when `!hasJournalData && !signedOff`.

### WP-6 — Research spike + parallel-run checklist

- `docs/migration/appfolio.md` — per-kind AppFolio report paths and canonical fields; column
  headers are best-guess candidates with explicit gate and update procedure documented.
- `docs/migration/parallel-run.md` — overlap-month checklist for the beta cutover.

### WP-7 — DoD close-out: cutover fixture, e2e, docs (this WP)

- Synthetic cutover fixture (`seed --org cutover`) provisioning an empty org (banks + CoA, no
  journal data) — wizard target for the onboarding e2e and `check-invariants` validation.
- `web/e2e/m7-onboarding.spec.ts` — happy path (entities → balances → verify $0 → sign off →
  working dashboard) + non-tying import path (shows ✗, sign-off button stays disabled / 409).
- `docs/adr/ADR-020-opening-balance-posting.md` — the M7 trust-accounting heart.
- `docs/adr/ADR-021-migration-toolkit.md` — toolkit architecture + verification gate.
- `docs/accounting.md` — "Migration / balance-forward cutover" section added.
- `private/TODO.md` §M7 boxes checked; research-spike box annotated as partially open.

---

## Integration Gate evidence (§D)

> The gate commands (from `private/planning/m7-wp7-brief.md` §Task 7.4) are:
>
> ```
> ./scripts/dev.ps1 reset-db      # migrate from blank (incl. AddImportToolkit)
> dotnet test LeaseBook.slnx --filter "FullyQualifiedName~SchemaGuardTests"
> $env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- seed --org demo
> $env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- seed --org demo   # idempotent
> $env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- check-invariants --org demo
> $env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- seed --org cutover
> # [import entities + balances + verify through wizard or API]
> $env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- check-invariants --org cutover
> dotnet build LeaseBook.slnx -c Debug
> dotnet test LeaseBook.slnx
> dotnet format --verify-no-changes --exclude src/LeaseBook.Web/Migrations
> cd web && npm run lint && npm run typecheck && npm run test && npm run build && npm run format:check
> npm run api:generate       # schema.d.ts no drift
> npm run e2e                # all specs green (incl. m7-onboarding)
> ```

**Run by the controller 2026-06-23 (HEAD `9127af0`, incl. the final whole-branch review fix wave). All green.**
Final whole-branch review (opus): **MERGE WITH FIXES** — all CLAUDE.md non-negotiables ✓; 2 Important + 2 cheap
fixes applied in one wave (`9127af0`): sign-off now **re-derives tie-out at sign-off time** (not the stored flag),
`MigrationNotTiedException` surfaces the clearing residual, the false "corrected re-import supersedes" doc promise
corrected, `InvariantChecks` I1–I5 doc. Roll-up Minors deferred to M8/backlog.

| Gate step | Result |
|---|---|
| `reset-db` → migrate from blank (AddImportToolkit applies cleanly) | ✅ "Done." — all migrations incl. `AddImportToolkit` apply to a blank DB |
| `SchemaGuardTests` (all three new tables have RLS) | ✅ green (in the Integration suite) |
| `seed --org demo` ×2 idempotent | ✅ idempotent (`ON CONFLICT DO NOTHING`) |
| `check-invariants --org demo` exit 0 (golden untouched) | ✅ "all clean across 1 org(s)" — demo golden untouched |
| `seed --org cutover` + import + `check-invariants --org cutover` exit 0 (clearing == 0) | ✅ "all clean" — after the full e2e import + sign-off, `migration_clearing` nets to $0.00 both bases, trust equation holds |
| `dotnet build LeaseBook.slnx -c Debug` | ✅ 0 warnings / 0 errors |
| `dotnet test LeaseBook.slnx` | ✅ **349/349 PASS** (18 Migrator · 12 Banking · 26 SharedKernel · 6 Architecture · 32 Operations · 91 Accounting · 164 Integration — the +1 is the stale-verification sign-off drift test) |
| `dotnet format --verify-no-changes` | ✅ clean |
| `npm run lint` | ✅ 0 errors (4 pre-existing react-refresh warnings) |
| `npm run typecheck` | ✅ clean |
| `npm run test` | ✅ **113/113 PASS** (post wizard-fix) |
| `npm run build` | ✅ (informational chunk-size > 500 kB warning only) |
| `npm run format:check` | ✅ clean |
| `npm run api:generate` (no schema drift) | ✅ NO DRIFT (`schema.d.ts` unchanged on regen against the live host) |
| `npm run e2e` (all specs incl. m7-onboarding) | ✅ **31 passed** (controller-verified twice — at `821c6bb` and re-locked at `9127af0` after the sign-off-re-derive fix; reset-db + seed demo + cutover each time): M2–M6 + smoke + 8 m7-onboarding |

**Two real bugs the §D e2e gate caught (and fixed) — the e2e-catches-drift lesson:**
1. `m7-onboarding.spec.ts` used `__dirname` (undefined in ESM) → the spec failed to load. Fixed with a `fileURLToPath` shim.
2. **Wizard auto-advance bug (functional):** after a successful entity import the status query invalidated → `entitiesImported` flipped true → `OnboardingPage` reactively re-derived `activeStep` and jumped to the balance step, *unmounting the success banner and skipping properties/units/tenants import* (the entity step imports four kinds via a radio). Fixed: step advancement is now **explicit** (a "Continue →" button), not reactive; the active step is pinned once loaded. The e2e was restructured to the corrected flow (37→31 total specs; m7-onboarding now 8 serial tests). Both bugs were missed by the WP-7a implementer's report (which claimed the e2e passed twice when it did not) — the controller's independent `npm run e2e` run is what surfaced them.

---

## ADR decisions

- **ADR-020** (opening-balance posting): `IBalanceForward.PostOpeningPositionAsync` +
  `AccountClass.MigrationClearing`. Per-position clearing-contra model; clearing nets to $0 iff
  the import ties; existing batched method + demo golden untouched.
- **ADR-021** (migration toolkit): `LeaseBook.Migrator` pure library; host orchestration in
  `Onboarding/`; tolerant parser seam + `appfolio-default` profiles (real headers gated);
  JSON-body import contract; hard sign-off gate (HTTP 409 + no side effect on non-tied).

---

## Deviations from the plan

1. **`Migration` → `Onboarding` namespace.** The design spec used `Migration/` as the host
   namespace placeholder. The implementation settled on `Onboarding/` for the host namespace,
   endpoint tags (`WithTags("Onboarding")`), and SPA feature directory, matching the user-visible
   concept (onboarding wizard) rather than the implementation mechanism. Functional design
   unchanged.

2. **JSON-body import contract (not multipart).** WP-3 in the spec did not prescribe multipart
   explicitly, but multipart was a natural assumption for file upload. At implementation, JSON body
   (`csvContent` string) was chosen for OpenAPI typing and generated-client clarity. Rationale in
   ADR-021. The files in scope (≤300-unit PM) are small enough that JSON-string encoding is not a
   constraint.

3. **WP-3/4/5 delivered in one integrated pass.** The spec split staging tables (WP-3),
   verification (WP-4), and SPA (WP-5) as sequential work packages. In practice, the host
   orchestration, staging tables, and verification service were built together in one pass because
   the `mapped_json` cross-import id-resolution store (WP-3) was a prerequisite for the balance
   import (WP-3), which in turn was a prerequisite for a meaningful verification run (WP-4). The
   SPA (WP-5) followed once the API surface was stable. No functional scope was dropped.

4. **WP-7 split across two controller tasks.** The WP-7 brief split the cutover fixture + e2e
   (Task 7.1/7.2) from the docs/retro (Task 7.3/7.4) so docs could be reviewed before the §D
   gate run. The §D evidence section is a placeholder to be filled by the controller.

---

## Known limitations (carried to M8)

1. **AppFolio column headers are unvalidated best-guess candidates.** `AppFolioProfiles` ships
   plausible header strings; real header validation is gated on real AppFolio export files being
   in hand. The tolerant parser + wizard column-remap are the safety net until then. The
   research spike procedure is documented in `docs/migration/appfolio.md §Gate`.

2. **`owner_balances` resolution picks the oldest Trust-purpose bank account.** When an org has
   multiple Trust-purpose bank accounts, `BalanceImportService.ResolveOperatingTrustAsync` picks
   the one with the earliest `created_at`. This is a reasonable default for single-trust-account
   PMs (the M7 target) but would misroute owner-equity opening positions for a multi-trust-account
   org. A per-row `external_bank_id` field in the owner-balance CSV would fix this; deferred
   pending real export format validation.

3. **`Features.Migration` dispatch token vs `Contracts`.** `GetMigrationVerificationData` lives in
   `Accounting.Features.Migration`, reachable from the host (the composition root). If this query
   is ever consumed by another module (not just the host), it should move to `Accounting.Contracts`.
   Noted for future cleanup.

4. **Accessibility pass deferred to M8.** The onboarding wizard's WCAG AA contrast, icon/label
   status badge pairing, full keyboard operability, and focus management on the sign-off flow are
   M8 scope (part of the M8.2 hardening pass).

5. **Late-fee policy settings UI (carried from M6).** The `LateFeePolicy` table exists; the
   settings screen for `grace_days`, `flat_fee`, `rate_bps`, `cap_amount` was deferred from M6 to
   M7 but was not in M7's build scope (M7 was fully consumed by the migration toolkit). Carries
   to M8.

6. **No supersede/correction workflow — re-import is figure-blind idempotency.** The opening-entry
   `source_ref` (`opening:{cutover}:{type}={subledgerId}`) identifies the subledger position, not the
   amount, so re-importing a balance with a **changed figure does NOT overwrite** the already-posted
   opening entry (the duplicate `source_ref` is caught and the row records as already-posted; the
   corrected figure never posts). The `import_batches.superseded` status is defined in the schema but
   not exercised by any M7 supersede path. To correct an already-posted opening figure **before
   sign-off**, re-provision the cutover org and re-import. An in-product supersede/correction workflow
   is **deferred to M8**. (The earlier "corrected re-import supersedes the prior batch and posts only
   changed rows" promise in `docs/accounting.md`, the design spec §5, and ADR-021 §3 was corrected to
   state this truth.)

7. **Held-PM-fees opening position is not imported in M7.** Only owner / deposit / bank / receivable
   balance kinds are imported. The held-PM-fees opening position (ADR-020 §5 table) is deliberately
   **not** imported — it would touch `pm_income`, which M7 keeps out of the import path. Any held-fee
   opening position surfaces as a `migration_clearing` residual the operator reconciles before
   sign-off. A first-class held-fee opening import is M8 scope.

---

## What M8 must absorb

1. **AppFolio real-column validation (research spike completion).** When real AppFolio
   export files are in hand: validate all column headers against `AppFolioProfiles`, update
   the candidate-header arrays, and remove the "best-guess" caveats in `docs/migration/appfolio.md`.
   Run the full import on a staging org.

2. **Real beta cutover run.** `seed --org beta` (or equivalent) → M7 wizard → real AppFolio
   export files → `check-invariants --org beta` exit 0 → PM signs off → go-live. This is the M7
   exit criterion that could not be met without real AppFolio export data.

3. **Accessibility pass on the onboarding wizard.** WCAG AA contrast, status badges, keyboard
   operability, focus management (see M8.2 in `private/TODO.md`).

4. **`owner_balances` multi-trust-account routing.** Per note 2 above — if the pilot org has
   more than one Trust-purpose bank account, the `ResolveOperatingTrustAsync` selection needs
   per-row disambiguation.

5. **Late-fee policy settings UI.** Configure `LateFeePolicy` rows via the Org Settings screen
   (carried from M6).

6. **Owner disbursement email notification (M8).** ACS send on disbursement confirmation is the
   M8 seam (carried from M6).
