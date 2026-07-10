# ADR-019: Bulk run engine and batch posting

- **Status:** Accepted
- **Date:** 2026-06-23
- **Milestone:** M6 (plan-local WP-1 — not a `docs/ROADMAP.md` WP id)

## Context

M6 implements three bulk operations — rent charging, late-fee assessment, and owner disbursements —
that each follow the same pattern: preview eligible targets, let the operator confirm a selection,
post the accounting events for each target, and record the outcomes. WP-1 establishes the shared
engine and tables so WP-2/3/4 can build the three concrete runs on top without duplicating
infrastructure.

Two design questions needed recording:

1. **How does Operations trigger accounting posts without crossing the ADR-007 module boundary?**
2. **How is idempotency guaranteed for repeat runs?**

---

## Decisions

### 1. `IBatchPosting` — write-direction cross-module port

`Modules.Operations` owns an `IBatchPosting` interface in its `Contracts` namespace. The interface
takes intent DTOs (owned by Operations) and returns journal-entry id maps. The host
(`BatchPostingAdapter`) implements the interface, translating intents into
`IAccountingEvents.PostAsync` calls. This follows the same ADR-007 pattern as M5's read-direction
`IOwnerStatementData` port, but in the write direction.

`Modules.Operations` never references `Modules.Accounting` types. The adapter (in the host) is the
only place both are in scope.

**Not implemented:** a new `PostEventBatch` command was originally specced. It is unnecessary —
`IAccountingEvents.PostAsync` is already public, scoped, and transaction-ambient. Looping it in the
adapter achieves the same result with less indirection.

### 2. `SourceRef` idempotency — reuse the existing index

`journal_entries` already carries a partial unique index `(org_id, source_ref) WHERE source_ref IS
NOT NULL`, and `PostingService` already throws `DuplicateSourceRefException` when a repeat is
attempted. WP-1 adds no new index; it simply threads the intent's `SourceRef` through to the
accounting event so the existing constraint deduplicates repeat runs.

**`SourceRef` key convention (record here for WP-2/3/4):**

| Run type | Target | `SourceRef` format |
|---|---|---|
| Rent | Lease | `rent:{year}-{month:00}:lease={leaseId}` |
| Late fee | Lease | `latefee:{year}-{month:00}:lease={leaseId}` |
| Disbursement fee | Owner | `disbursement-fee:{year}-{month:00}:owner={ownerId}` |
| Disbursement | Owner | `disbursement:{year}-{month:00}:owner={ownerId}` |

### 3. Run history tables — append-only, RLS-enforced

Two tables are added:

- `bulk_runs` — one header row per committed run. `summary_json` (jsonb) carries the
  posted/skipped/excluded counts and total. `run_type`, `period_year`, `period_month` enable the
  UI history view.
- `bulk_run_items` — one row per target per run. `snapshot_json` (jsonb) holds per-item metadata
  (amounts, entry ids, source_refs) chosen by the strategy.

Both tables are `IOrgScoped` with RLS enabled via `EnableOrgRls`. They are written by
`AppDbContext.SaveChangesAsync`, which also auto-produces `audit_events` rows for every insert
(the "one audit row per committed run" requirement is satisfied by the existing convention, not
explicit code).

### 4. Run engine pattern

`RunEngine.ConfirmAsync` runs inside the ambient org-scoped transaction. It:
1. Creates a `BulkRun` header (unseeded summary).
2. Calls `strategy.ConfirmAsync(run, selectedIds, posting, ct)` — the strategy owns the
   per-item posting loop and exception handling.
3. Aggregates item counts and patches `summary_json` on the header (pre-save, still in Added
   state — no UPDATE needed).
4. Calls `SaveChangesAsync` once, persisting the run + all items atomically.

Strategies are expected to catch `DuplicateSourceRefException` (→ `Skipped`) and
`AccountPeriodLockedException` or `PeriodClosedException` (→ `Excluded`) per item; no unhandled
posting exception should escape. The no-op test strategy (WP-1) never triggers these.

### 5. Audit seam

The run engine does **not** call any explicit audit API. `AppDbContext.SaveChangesAsync` writes one
`audit_events` row per `IOrgScoped` insert — `BulkRun` is `IOrgScoped`, so every run automatically
gets an audit trail via the existing convention. This avoids an awkward cross-module audit port.

---

## Consequences

- WP-2/3/4 implement `IRunStrategy` and register them via `OperationsModuleServiceCollectionExtensions`.
- The `IBatchPosting` port and adapter are complete; WP-2/3/4 need only call the adapter's methods
  with the appropriate intents and source_ref keys.
- The idempotency guarantee is provided by the accounting layer; Operations strategies do not need
  to check for duplicate source refs before posting — the exception is caught and recorded as
  `Skipped`.
- `ModuleBoundaryTests` enforces that `Modules.Operations` references only `SharedKernel`;
  `SchemaGuardTests` enforces RLS on both new tables.

## Revisit trigger

Reopen the `SourceRef` key convention if a run type appears whose targets cannot be keyed as
`{runType}:{year}-{month:00}:{target}` (e.g. ad-hoc or non-monthly runs), and the
one-transaction confirm path if per-item posting volume at real scale makes a single atomic
run a lock-contention or timeout problem (then consider chunked confirms with a run-level
resume, recorded as a new ADR).
