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
- **`AppFolioImportCatalog`** — the executable `appfolio-default` catalog. Each typed definition
  owns one canonical route token, workflow family, stable persisted name, profile identifier and
  header mappings, plus its CSV-to-row binder. The definition instance is the in-process kind
  identity; the persisted names remain compatible with existing `import_batches` and audit data.
  Lookup is case-insensitive and ignores underscores for route compatibility, but rejects the old
  numeric aliases that leaked through enum parsing.
- **Typed rows** (`OwnerRow`, `PropertyRow`, `UnitRow`, `TenantLeaseRow`, `OwnerBalanceRow`,
  `DepositLiabilityRow`, `BankBalanceRow`, `TenantReceivableRow`, `HeldPmFeeRow`) — canonical in-memory
  representations that the host's import services consume.

The former `EntityKind`, `AppFolioProfiles`, and `EntityImporter` parallel surfaces are removed.
Kind-specific parsing now enters through `AppFolioImportDefinition<TRow>.Read(Stream)`.

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
  importer. Its statically typed application table must cover the catalog's entity family exactly.
- **`BalanceImportService`** — parses balance CSVs, resolves external ids to LeaseBook ids via
  `ExternalIdResolver` (reads prior `import_rows`) and bank name-matching, then calls
  `IBalanceForward.PostOpeningPositionAsync` per valid row — all in one ambient RLS transaction.
  Its statically typed planning table must cover the catalog's balance family exactly. Both tables
  close over the definition's row type before exposing a non-generic invocation delegate; they use
  no reflection, row casts, or DI registry.
- **`VerificationService`** — dispatches `GetMigrationVerificationData` via `ISender` (no
  cross-module SQL), builds the line-by-line variance report, persists a `migration_verifications`
  row, and enforces the hard sign-off gate (see §5 below).
- **`OnboardingStatusEndpoints`** — derives the six-flag wizard state from existing data on the
  ambient RLS transaction and returns the canonical cutover date derived from Accounting's immutable
  opening entries (no dedicated status table).

### 3. Staging tables

Three tables are added by the `AddImportToolkit` migration. All go through the migrations RLS
helper (`EnableOrgRls`) and are covered by `SchemaGuardTests`.

**`import_batches`** — one row per CSV upload, recording the entity kind, mapping profile,
filename, row/error counts, and batch status (`posted` / `posted_with_errors`; supersession is
recorded on the successor row via `supersedes_batch_id` — the runtime role has no UPDATE grant, so
a status flip is structurally impossible). The audit trail for what was uploaded and when.

> **Amended by M8 WP-7.** Both deferrals recorded here are now closed. (1) A **pre-sign-off
> supersede workflow** exists: `POST /api/onboarding/import-balances/{kind}/supersede` compares
> corrected figures against the live opening positions per `source_ref` family, posts a linked
> reversal (dated at cutover) plus a corrected revision (`#r{N}` suffix) per changed position,
> leaves unchanged positions untouched, and records lineage on the successor batch
> (`supersedes_batch_id`). After sign-off the endpoint returns 409 — corrections become ordinary
> ledger reversals. (2) The **held-PM-fees opening position** (ADR-020 §5) is now imported as the
> fifth balance kind rather than surfacing as a clearing residual the operator reconciles by hand.

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

- A documented `appfolio-default` definition per import kind in `AppFolioImportCatalog`.
- A tolerant parser seam: missing required headers surface as `RowError`s. Inline column remapping
  is not implemented; operators must use a supported candidate header or the catalog must be
  updated after a real export is verified.
- A private research spike documenting what needs validation and how to update the profiles once
  real exports arrive; unvalidated mappings are not presented as public product documentation.

The validation gate is maintained with the private migration research. Plugging in real headers is
a string-array update in `AppFolioImportCatalog.cs` — no architectural change.

**Consequence of this deferral:** the M7 exit criteria use a synthetic cutover fixture (CSV files
with known-good figures) rather than real AppFolio exports. The real cutover run on a
staging org is the M8/operator step that the research spike unblocks.

### 5. Import contract — JSON body, not multipart

The import kind is a route token. The endpoints accept `{ mappingProfile, filename, cutoverDate,
csvContent }` as a JSON body, where `csvContent` is the CSV text as a JSON string. Only
`appfolio-default` is supported; null or whitespace selects it, and any other supplied identifier is
rejected. The resolved definition owns the profile identifier persisted with the batch.

`cutoverDate` is required but is not independent per upload. The first balance position that posts
establishes one journal-derived organization cutover date (ADR-020 §6); later balance imports,
supersedes, and verification must match it or receive HTTP 409 `cutover_date_mismatch`. Before the
first posting the UI leaves the field blank and required rather than defaulting it to today; afterward
the onboarding-status response restores it as read-only.

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

- **The catalog is the only place that knows AppFolio CSV shapes.** Changing a column profile is
  local to `LeaseBook.Migrator`. Adding a kind requires one typed catalog definition and one real
  host application/planning registration; endpoint family allowlists and onboarding status derive
  from the catalog. The explicit SPA list remains separately owned because no discovery endpoint is
  introduced.
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

If a second property-management source (Buildium, Rentec Direct) is ever onboarded, evaluate a
provider seam instead of generalizing the AppFolio catalog speculatively, and decide whether the
research-spike process should become a documented operator runbook.
