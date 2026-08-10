# ADR-019: Bulk run engine and batch posting

- **Status:** Accepted
- **Date:** 2026-06-23
- **Milestone:** M6 (plan-local WP-1 — not a `docs/ROADMAP.md` WP id)
- **Amended by:** [ADR-028](ADR-028-platform-capability-model.md) — adds the capability snapshot the
  run engine freezes at confirm entry (§4a below) and the chunked-confirm constraint in the revisit
  trigger
- **Amended 2026-08-09 (self):** strategies plan and the engine executes (§4b); §4a revised so no
  capability set reaches a strategy; §2 corrected — source-ref keys must not be derived from
  `RunType`

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

**Correction (2026-08-09): the generalised shape does not describe this table, so do not derive a key
from `RunType`.** The revisit trigger below states the convention as
`{runType}:{year}-{month:00}:{target}`, and two of the four rows above do not fit it: `latefee` is not
`LateFee` mechanically lowercased, and `disbursement-fee` is not the name of a run type at all. A
helper that built the prefix from `RunType` would therefore emit different keys for those two rows
than the ones already committed — and because idempotency rests on a partial unique index, the symptom
would not be an error. It would be **duplicate postings**: the new key would not collide with the old
one, so a re-run of an already-posted period would charge every tenant a second time.

Each strategy therefore builds its own finished key and the engine never constructs one. The
convention's value is that it is _stable_, not that it is _centralised_.

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
3. Calls `strategy.PlanAsync(period, selectedIds, ct)` and drives the returned plan itself — the
   engine owns the per-item posting loop, the outcome mapping, item construction and serialization
   (amended 2026-08-09 — see below).
4. Aggregates item counts and patches `summary_json` on the header (pre-save, still in Added
   state — no UPDATE needed), recording the capability version alongside the counts.
5. Calls `SaveChangesAsync` once, persisting the run + all items atomically.

Every per-item refusal — a duplicate source ref (→ `Skipped`), a locked bank period, a closed
accounting period or a breached reserve floor (→ `Excluded`) — is returned by `IBatchPosting` as a
`PostOutcome` rather than thrown, and the engine records it and carries on. Anything that does throw
is not per-item and is deliberately not caught. The no-op test strategy (WP-1) never triggers these.

### 4a. Capability snapshot (amendment, 2026-08-03 — ADR-028; revised 2026-08-09)

The engine resolves the capability set **once, at `ConfirmAsync` entry**, inside the ambient
transaction and not from cache. One run therefore decides every item under one capability set even if
an operator flips a flag while it is in flight, and `summary_json` records the version it ran under.

**Revised 2026-08-09: no capability set is passed to the strategy.** The original amendment made
`IRunStrategy.ConfirmAsync` take a `RunCapabilities` parameter, reasoning that a parameter cannot be
lost silently where an ambient lookup can. With the planning move in §4b, the strategy has no posting
loop to lose it in: the engine is the only thing that resolves, the only thing that posts, and — under
the chunked confirm the revisit trigger contemplates — the only thing that would resume. Removing the
parameter shrinks the surface that could re-resolve rather than widening it, and it makes the
reachability rule below structural: a strategy that is handed no capability set cannot derive a posted
value from one.

It follows that a strategy must not reach a capability by another route either — no injected
`ICapabilitySnapshot`, no collaborator that resolves one. Whoever first needs a capability inside a
strategy has to state which of the two they are doing, and the answer is almost always that the gate
belongs above the plan.

Capabilities are **reachability-only**. A capability may gate whether a posting path runs at all
(endpoint, command, or strategy selection); it may never change the lines or amounts an existing
business event produces. Money-affecting parameters live in `OrgSettings`. Concretely: no value read
off `RunCapabilities` may become an argument to an Accounting command, business event, or
posting-template input.

`RunCapabilities` and its `ICapabilitySnapshot` port are declared by Operations (ADR-007), not by
`SharedKernel` and not by the Capabilities module: every module depends on `SharedKernel` and
Accounting is a posting path, so a capability type there would be reachable from posting code with
every reference-graph architecture test still green. The host adapter maps the resolved
`CapabilitySet` into the Operations view on the ambient RLS transaction, opening no scope,
transaction or second connection.

### 4b. Strategies plan; the engine executes (amendment, 2026-08-09)

`IRunStrategy.ConfirmAsync` is replaced by `PlanAsync`, which returns one `RunPlanItem` per selected
target — either `PlannedPosting` (post this intent, worth this much, and record it like this) or
`PlannedExclusion` (record this target as skipped or excluded, for this reason). It posts nothing,
persists nothing, and no longer sees the `BulkRun` header or the `IBatchPosting` port at all.

Superseded by this: §4 step 3's "the strategy owns the per-item posting loop and exception handling",
and the sentence that followed it directing strategies to catch `DuplicateSourceRefException` and the
period exceptions per item. Neither was ever domain knowledge. Each of the three strategies carried
its own copy of the same 60-line skeleton — the loop, ten exception filters, the `BulkRunItem.Create`
calls and the JSON serialization — so a fourth run type had to reproduce all of it correctly from
memory, and the money-path classification of a refusal could drift between run types with nothing
failing. It now exists once, in `RunEngine`.

The engine contributes exactly three keys to an item's `snapshot_json` — `entryId`, `feeEntryId` and
`reason` — because only it holds the outcome. Everything else on the item is the strategy's own
vocabulary, copied through unread.

`ReserveFloorException` is translated by `BatchPostingAdapter` inside its disbursement branch rather
than in its outer `catch`, so the classification is scoped to the one posting that can raise it.
Accounting's reserve-floor guard runs on `OwnerDisbursed` and nothing else; the same exception
reaching a rent or late-fee posting would mean something is wrong in the layer below, and a
uniformly-applied catch would file that away as an ordinary per-item exclusion on the money path.

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
  to check for duplicate source refs before planning — the refusal comes back to the engine and is
  recorded as `Skipped`.
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

This constraint is **unchanged** by the 2026-08-09 amendments; only its owner is named more
precisely. It was written when the set was a strategy parameter, and might now read as having been
about that parameter. It was not. The obligation is on whatever resumes a chunked run, and that is
`RunEngine` — the strategy is a pure planner and holds no capability state to carry.
