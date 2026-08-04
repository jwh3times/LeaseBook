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

| Run type         | Target | `SourceRef` format                                   |
| ---------------- | ------ | ---------------------------------------------------- |
| Rent             | Lease  | `rent:{year}-{month:00}:lease={leaseId}`             |
| Late fee         | Lease  | `latefee:{year}-{month:00}:lease={leaseId}`          |
| Disbursement fee | Owner  | `disbursement-fee:{year}-{month:00}:owner={ownerId}` |
| Disbursement     | Owner  | `disbursement:{year}-{month:00}:owner={ownerId}`     |

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
2. Resolves the capability set once, at confirm entry, through the `ICapabilitySnapshot` port
   (amended 2026-08-03 — see below).
3. Calls `strategy.ConfirmAsync(run, selectedIds, posting, capabilities, ct)` — the strategy owns
   the per-item posting loop and exception handling.
4. Aggregates item counts and patches `summary_json` on the header (pre-save, still in Added
   state — no UPDATE needed), recording the capability version alongside the counts.
5. Calls `SaveChangesAsync` once, persisting the run + all items atomically.

Strategies are expected to catch `DuplicateSourceRefException` (→ `Skipped`) and
`AccountPeriodLockedException` or `PeriodClosedException` (→ `Excluded`) per item; no unhandled
posting exception should escape. The no-op test strategy (WP-1) never triggers these.

### 4a. Capability snapshot (amendment, 2026-08-03 — ADR-028)

`IRunStrategy.ConfirmAsync` takes a `RunCapabilities` parameter. The engine resolves it **once, at
`ConfirmAsync` entry**, inside the ambient transaction and not from cache, and hands the same value
to the strategy for the whole run. One run therefore decides every item under one capability set even
if an operator flips a flag while it is in flight, and `summary_json` records the version it ran
under.

Two properties this fixes, neither of which the pre-amendment shape had:

- It is a **parameter, not an ambient lookup**. Today the freeze would hold either way, because
  confirm runs inside one request transaction — but only incidentally, which is the problem the
  revisit trigger below names.
- Capabilities are **reachability-only**. A capability may gate whether a posting path runs at all
  (endpoint, command, or strategy selection); it may never change the lines or amounts an existing
  business event produces. Money-affecting parameters live in `OrgSettings`. Concretely: no value
  read off `RunCapabilities` may become an argument to an Accounting command, business event, or
  posting-template input.

`RunCapabilities` and its `ICapabilitySnapshot` port are declared by Operations (ADR-007), not by
`SharedKernel` and not by the Capabilities module: every module depends on `SharedKernel` and
Accounting is a posting path, so a capability type there would be reachable from posting code with
every reference-graph architecture test still green. The host adapter maps the resolved
`CapabilitySet` into the Operations view on the ambient RLS transaction, opening no scope,
transaction or second connection.

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

**Any chunked-confirm design must carry the capability snapshot across chunk boundaries.** A chunk
boundary is a new transaction, so the current guarantee — the whole confirm runs inside one request
transaction — does not survive chunking, and the freeze would evaporate silently: a resume that
re-resolved per chunk would post the tail of a run under capabilities the head never saw, and the
run's own `summary_json` would still claim a single version. The snapshot resolved at the first
chunk's confirm entry must be persisted with the run's resume state and re-supplied to every later
chunk; a chunk that cannot be given that exact set must fail rather than resolve its own. Note that
a "flip mid-run" test that only exercises the first chunk passes regardless, so the coverage has to
move with the design.
