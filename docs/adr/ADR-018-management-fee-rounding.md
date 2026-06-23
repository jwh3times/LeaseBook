# ADR-018 — Management-fee rounding for the owner disbursement run

**Status:** Accepted — M6 WP-4
**Date:** 2026-06-23

## Context

The owner disbursement run (WP-4) folds the management-fee assessment into a single per-owner
posting batch. A rounding rule is required so that fee amounts are deterministic and auditable.

## Decision

`fee = Round(equity × bps / 10000, 2, MidpointRounding.AwayFromZero)`

All arithmetic uses `decimal` (C#) / `NUMERIC(14,2)` (Postgres). Never `float`/`double`.

**Ordering:** `ManagementFeeAssessed` is posted FIRST; this reduces owner equity by `fee` before
the `OwnerDisbursed` guard checks the reserve floor (`GuardReserveFloorAsync` sees equity_after_fee).

**Property-level fee override (Phase-1 simplification):** The disbursement aggregates the
owner's entire equity across all properties; per-property equity decomposition is not performed.
Therefore only the owner-level default bps (`owners.default_mgmt_fee_bps`) is used — the
`propertyId = null` resolution path of `IManagementFeeConfig`. Property-level overrides require
per-property equity decomposition (future work).

**Owners with no configured bps** receive `fee = 0` (no fee entry posted).

**Equity basis:** Cash+both (the owner's distributable cash balance per §C.6/P30). This is the
`Operating` field from `GetOwnerBalances`, which queries `owner_equity` lines with
`basis IN ('cash', 'both')`. Accrual-only charges (e.g. `RentCharged`) do NOT increase
distributable equity — only cash receipts (`PaymentReceived`, `DepositApplied`, etc.) do.

## Alternatives considered

- **Truncate (floor):** Favors PM but inconsistent with rounding convention.
- **Banker's rounding:** Statistically unbiased but PM industry expects deterministic half-up.
- **Property-level fee:** Requires per-property equity decomposition not implemented in Phase 1.
- **Accrual equity basis:** Would include uncollected rent — violates trust accounting (you can't
  disburse money not yet in the trust account).

## Consequences

- `MgmtFee.Compute(equity, bps)` is a pure static function, unit-testable without DB.
- Golden-file lock of fee + disburse amounts in integration tests provides regression safety.
- Phase-2 work: decompose equity by property and apply property-level bps overrides.
- The DuplicateSourceRefException catch is the idempotency backstop if the run is retried with
  the same source_refs; if equity is zero after disbursement, a re-run excludes the owner
  (correct — there is nothing left to disburse).
