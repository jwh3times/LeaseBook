# ADR-021: Migration toolkit architecture & verification gate

- **Status:** Accepted
- **Date:** 2026-06-23
- **Deciders:** Engineering
- **Milestone:** M7

## Context

M7 turns the `LeaseBook.Migrator` placeholder into a working toolkit for cutting over a PM from
AppFolio to LeaseBook. The core problem: AppFolio exports CSV files; LeaseBook must ingest them
reliably, post opening balances that can be verified, and block go-live until the numbers tie. A
bad migration that leaves owner balances off by even a dollar destroys trust immediately.

Five questions needed recording:

1. **Where does CSV parsing live?** Mixed into the host (alongside posting and DB) or isolated?
2. **How does the host post opening balances without crossing the ADR-007 boundary?**
3. **What are the staging tables for?**
4. **Why are real AppFolio column headers deferred to a research spike?**
5. **How is the import contract shaped (JSON body vs multipart)?**

## Decision

### 1. `LeaseBook.Migrator` — pure parse/map/validate library

`LeaseBook.Migrator` references `SharedKernel` only (enforced by `ModuleBoundaryTests`). It knows
nothing about the database, posting, or HTTP. Its public surface:

- **`CsvImporter.Read<TRow>(Stream, ColumnMappingProfile, Func<RowContext, TRow?>)`** — tolerant,
  collect-and-continue CSV ingestion. A malformed or missing field records a `RowError` and returns
  null from the bind delegate; the remaining rows keep going. One bad row never sinks the batch.
- **`RowContext`** — carries canonical cell values for the current row and an explicit `Reject<T>`
  helper (field, reason) that records an error and signals skip, rather than throwing.
- **`ColumnMappingProfile`** — a list of `(canonicalField, candidateHeaders[], required)` records.
  The profile resolves actual CSV headers against candidates; missing required columns produce
  top-level errors before any rows are processed.
- **`AppFolioProfiles.For(EntityKind)`** — the `appfolio-default` profiles (one per entity kind).
  Header candidates are best-guess strings (see §4 below). Plugging in the real
  headers is a data edit to these profiles, not a code change.
- **Typed rows** (`OwnerRow`, `PropertyRow`, `UnitRow`, `TenantLeaseRow`, `OwnerBalanceRow`,
  `DepositLiabilityRow`, `BankBalanceRow`, `TenantReceivableRow`) — canonical in-memory
  representations that the host's import services consume.
- **`EntityImporter`** — static binders that call `CsvImporter.Read<TRow>` with the type-specific
  bind logic, centralizing all CSV-to-domain mapping in one place.

The isolation makes the parser **fully unit-testable in isolation**: no Testcontainers, no HTTP,
no DI. The `LeaseBook.Tests.Migrator` project exercises tolerant ingestion, row-level error
reporting, mapping-profile resolution, and malformed/missing fields against in-memory CSV strings.

### 2. Host orchestration in `src/LeaseBook.Web/Onboarding/`

The host is the composition root and may inject published Accounting contracts directly — it is not
subject to the Operations-style "can't reference Accounting types" constraint (same precedent as
`DemoJournalSeed`). The orchestration namespace is `Onboarding` (not `Migration` — the design spec
used `Migration/` as a placeholder; the implementation settled on `Onboarding/` to match the SPA
feature and endpoint naming).

- **`EntityImportService`** — parses entity CSVs, creates Directory rows via `ISender` commands
  (existing Directory write paths), and records `import_rows` staging data. External-id→LeaseBook-id
  mappings are persisted in `import_rows.mapped_json` for downstream consumption by the balance
  importer.
- **`BalanceImportService`** — parses balance CSVs, resolves external ids to LeaseBook ids via
  `ExternalIdResolver` (reads prior `import_rows`) and bank name-matching, then calls
  `IBalanceForward.PostOpeningPositionAsync` per valid row — all in one ambient RLS transaction.
- **`VerificationService`** — dispatches `GetMigrationVerificationData` via `ISender` (no
  cross-module SQL), builds the line-by-line variance report, persists a `migration_verifications`
  row, and enforces the hard sign-off gate (see §5 below).
- **`OnboardingStatusEndpoints`** — derives the six-flag wizard state from existing data on the
  ambient RLS transaction (no dedicated status table).

### 3. Staging tables

Three tables are added by the `AddImportToolkit` migration. All go through the migrations RLS
helper (`EnableOrgRls`) and are covered by `SchemaGuardTests`.

**`import_batches`** — one row per CSV upload, recording the entity kind, mapping profile,
filename, row/error counts, and batch status (`staged` / `posted` / `superseded`). The audit trail
for what was uploaded and when.

> **Re-import is figure-blind idempotency, not supersede (M7).** The `source_ref`
> (`opening:{cutover}:{type}={subledgerId}`) identifies the subledger position, not the amount, so a
> re-import with a **changed figure does NOT overwrite** the already-posted opening entry — the
> duplicate `source_ref` is caught (`DuplicateSourceRefException` → row recorded as already-posted)
> and the corrected figure never posts. The `superseded` status is **defined in the schema but not
> exercised by any M7 supersede path**: correcting an already-posted opening figure before sign-off is
> done by re-provisioning the cutover org and re-importing. An in-product supersede/correction
> workflow is **deferred to M8**. Note also that the **held-PM-fees opening position (ADR-020 §5) is
> not imported in M7** — only owner / deposit / bank / receivable kinds are; held fees would touch
> `pm_income` (kept out of the import path) and instead surface as a migration-clearing residual the
> operator reconciles before sign-off.

**`import_rows`** — one row per CSV data line. Stores the original parsed cells (`raw_json`),
canonical fields (`mapped_json`), row status, and — for balance rows that posted successfully —
`resulting_journal_entry_id`. The `mapped_json` is also the cross-import id-resolution store: the
entity importer writes `{ externalId, leaseBookId }` there; the balance importer reads it to
resolve external owner/tenant ids to LeaseBook UUIDs.

**`migration_verifications`** — immutable verification snapshots. Each verification run writes a
new row (never upserts). Sign-off writes a second new row with `signed_off_by` / `signed_off_at`
pre-populated, leaving the original unsigned row intact for auditability. The table is
`RevokeAppendOnly` — the runtime role has no UPDATE grant, making the immutability structural.

### 4. AppFolio column profiles — deferred to the research spike

The concrete AppFolio column header strings are **not validated** — real export files are not yet
in hand. M7 ships:

- A documented `appfolio-default` profile per entity kind in `AppFolioProfiles.For(EntityKind)`.
- A tolerant parser seam: unrecognized headers surface as `RowError`s; the wizard lets the
  operator remap columns inline.
- A private research spike documenting what needs validation and how to update the profiles once
  real exports arrive; unvalidated mappings are not presented as public product documentation.

The validation gate is maintained with the private migration research. Plugging in real headers is
a string-array update in `AppFolioProfiles.cs` — no architectural change.

**Consequence of this deferral:** the M7 exit criteria use a synthetic cutover fixture (CSV files
with known-good figures) rather than real AppFolio exports. The real cutover run on a
staging org is the M8/operator step that the research spike unblocks.

### 5. Import contract — JSON body, not multipart

The import endpoints accept `{ entityKind, mappingProfile, filename, cutoverDate, csvContent }`
as a JSON body, where `csvContent` is the CSV text as a JSON string.

**Why not multipart/form-data:** JSON bodies are typed in the OpenAPI schema; the generated
TypeScript client (`api:generate`) produces a strongly-typed `ImportEntitiesRequest` that the
wizard uses directly. Multipart handling requires custom binders and produces a weaker OpenAPI
representation. The CSV files in scope are small (hundreds of rows, kilobytes) — the size argument
for streaming multipart does not apply.

**Accepted trade-off:** large imports (tens of thousands of rows) would be inefficient as a JSON
string. At the target scale (≤ 300 units; ~150-unit pilot) this is not a constraint. If a
larger-scale import is ever needed, re-evaluate streaming with a dedicated ADR at that time.

### 6. Hard sign-off gate

Go-live is blocked until:

1. `IsTied == true`: all variance lines are zero AND the `MigrationClearing` residual is $0.00 in
   **both** bases.
2. The PM clicks **Approve** in the wizard.

If the referenced verification row is not tied, `POST /api/onboarding/signoff/{id}` returns HTTP
409 (`not_tied`) with no side effect — no DB write, no audit row. If tied, a new
`migration_verifications` row with `signed_off_by` / `signed_off_at` is inserted and a
`migration-signed-off` `audit_events` row is written explicitly (in addition to the auto-audit
the `AppDbContext` interceptor produces for every insert on an `IOrgScoped` entity).

The empty-dashboard takeover (`HasJournalData` flag from `GET /api/onboarding/status`) disappears
once the org has any journal data — so an org with operational activity (the demo org) is never
redirected into onboarding.

## Consequences

- **The parser is the only place that knows CSV shapes.** Changing a column profile or adding a
  new entity kind touches `LeaseBook.Migrator` only — the host import services are profile-agnostic.
- **The `Onboarding` namespace vs the spec's `Migration` namespace.** The implementation settled on
  `Onboarding` for the host namespace, endpoint tags (`WithTags("Onboarding")`), and SPA feature.
  The spec used `Migration` as a working name. This is a cosmetic deviation; the ADR-007 boundary
  and the functional design are unchanged.
- **`ModuleBoundaryTests` verifies the assembly boundary.** `LeaseBook.Migrator` referencing only
  `SharedKernel` is compiler-enforced via the test. The no-cross-module-SQL half (the host's balance
  import calling `IBalanceForward` rather than writing journal SQL) is a code-review rule.
- **`Features.Migration` dispatch token vs `Contracts`.** The `GetMigrationVerificationData` query
  record lives in `Accounting.Features.Migration`, not in `Accounting.Contracts` (where
  `IBalanceForward` lives). It is dispatched via `ISender` from the host, which is the composition
  root and may see internal Accounting namespaces. This is a convention note for future cleanup: if
  the query is ever exposed to another module, it should move to `Contracts`.

## Revisit trigger

If a second property-management source (Buildium, Rentec Direct) is ever onboarded, evaluate
whether `AppFolioProfiles` should become a plugin registry and whether the research-spike process
should be formalized into a documented operator runbook. The parser seam is already configuration-
driven; the registry shape would be the only new piece.
