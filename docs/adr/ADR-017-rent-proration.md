# ADR-017 — Rent Proration Method (M6 WP-2)

**Date:** 2026-06-23  
**Status:** Accepted  
**Milestone:** M6 WP-2

---

## Context

The monthly rent charge run (WP-2) must prorate rent for leases whose term boundary falls
within the charge period: a tenant who moves in mid-month should only be charged for the days
occupied, and a lease that ends mid-month should not be charged for days after the end date.

Three design questions needed recording:

1. **How many days does a partial month span?** (Which days count, and is the boundary day included?)
2. **How is the prorated amount computed from the daily fraction?** (Floating-point risk, rounding direction.)
3. **What is the "already done" check for re-runs?** (Idempotency: how does preview detect a
   prior post without crossing module boundaries?)

---

## Decisions

### 1. Actual-days, inclusive, single-division

Proration uses **actual calendar days in the month** as the denominator (`DateTime.DaysInMonth`).
Both the move-in day and the move-out day are **counted** (inclusive). A tenant who moves in on
day 16 of a 31-day month occupies days 16–31 = **16 days**, not 15.

Rationale:
- Inclusive counting is standard in residential property management.
- It matches the most common tenant expectation ("I moved in on the 16th, so I pay from the 16th").
- It is trivially verifiable against the lease start/end dates visible in the UI.

### 2. Single multiplication then division; `AwayFromZero` rounding

The formula is:

```
proratedAmount = Math.Round(monthlyRent × daysOccupied / daysInMonth, 2, MidpointRounding.AwayFromZero)
```

No intermediate rounding. `decimal` arithmetic (not `double`) throughout.
`AwayFromZero` matches the North Carolina convention and common PM practice for money rounding.

**Example (March 2026, 31 days, move-in Mar 16, rent $1,620):**
- `daysOccupied` = 31 − 16 + 1 = 16
- `1620 × 16 / 31` = 836.129032…
- Rounded: **$836.13**

### 3. "Already done" check via `IPostedSourceRefs` port (ADR-007 compliant)

The rent-run source_ref for a lease is `"rent:{year}-{month:00}:lease={leaseId}"` (per ADR-019).
The preview step checks which candidate keys already exist in `journal_entries.source_ref` by
dispatching through the **`IPostedSourceRefs`** port (declared in `Modules.Operations.Contracts`,
implemented by `PostedSourceRefsAdapter` in the host). The adapter dispatches
`GetExistingSourceRefs` to the Accounting module via `ISender`.

**Operations never reads `journal_entries` directly.** The module boundary is maintained; the
adapter is the only place Accounting types are in scope.

### 4. Charge date

The `entry_date` for a rent charge is the **first day of the period month** (e.g. `2026-03-01`
for the March 2026 run). This simplifies period locking: the single date is predictable and
auditable, and all rent charges for a period fall in the same accounting period.

### 5. Exception handling in `RentRunStrategy.ConfirmAsync`

Per-item exceptions from `IBatchPosting.PostRentChargesAsync` are caught and mapped:

| Exception type name | Outcome |
|---|---|
| `DuplicateSourceRefException` | `Skipped` (already posted in a prior run) |
| `AccountPeriodLockedException` | `Excluded` (period is locked) |
| `PeriodClosedException` | `Excluded` (period is closed) |

Type detection uses `ex.GetType().Name` (string comparison, not type reference) to avoid an
ADR-007 violation: `Modules.Operations` must not reference `Modules.Accounting` types.

Leases with `Rent == 0` and leases whose term ends before the period start are
surfaced as `Exceptions` in the preview (not rows), so the operator sees them explicitly.

---

## Consequences

- `Proration.Charge` is a pure, stateless static method tested by table-driven unit tests
  whose figures are the ground-truth reference (golden-locked).
- The preview's "AlreadyDone" flag uses a single batch read (all candidate keys in one call),
  not a per-lease query.
- The idempotency guarantee for re-runs comes from `DuplicateSourceRefException` → `Skipped`
  in the confirm path, not from an upfront exclusion check in the strategy.
- Trust accounting invariants are maintained: proration is a read-only computation; the
  double-entry entry is posted by `PostingService` using the rounded amount with no further
  manipulation.
