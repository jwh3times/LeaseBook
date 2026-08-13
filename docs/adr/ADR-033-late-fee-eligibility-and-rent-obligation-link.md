# ADR-033: Late-fee eligibility and rent-obligation linkage

- **Status:** Accepted (amended by ADR-034)
- **Date:** 2026-08-13
- **Deciders:** Jerry Holland
- **Amends:** [ADR-006](ADR-006-posting-template-catalog.md) and
  [ADR-019](ADR-019-bulk-run-engine-and-batch-posting.md)

[ADR-034](ADR-034-computed-fifo-receivable-allocation.md) extends oldest-charge-first allocation to
delinquency aging and persists charge due dates. The eligibility and rent-obligation linkage
decision here remains in force.

## Context

The late-fee run aged tenant receivables from journal entry dates, evaluated them at the selected
month's end, and posted fees on that month's first day. Those dates are accounting mechanics rather
than the lease's contractual due date or the day the assessment actually occurs. Its lease-and-month
idempotency key also inferred the assessed rental payment instead of identifying it. N.C. G.S.
42-46 permits a residential late fee only when a rental payment is at least five calendar days late,
counting the day after the due date as day one, and permits only one fee for that rental payment.

## Decision

For a rent period, the effective lease policy supplies the contractual due date. The next calendar
day is late day one, and the first eligible assessment date is the due date plus the greater of five
days or the lease's configured threshold.

Phase 1 uses the server's current UTC calendar date as the assessment date for both preview and
confirm. An operator cannot select a future assessment date. Confirm recalculates eligibility and
posts `FeeCharged(FeeKind.Late)` on that confirmation-date assessment.

The run resolves the period's canonical, unreversed `RentCharged` entry and excludes an obligation
that is no longer open. For this decision, tenant-receivable reductions through the assessment date
apply to positive charges oldest first; a later unrelated charge therefore cannot make settled rent
eligible again. The late-fee intent and event carry the open rent entry id. The fee journal header
stores it as `assesses_entry_id`, enforced by an org-safe composite foreign key and a unique partial
index on `(org_id, assesses_entry_id)`. Its idempotency key is
`latefee:rent-entry={rentEntryId}`. The relation, not tenant plus month, enforces one assessment per
rental payment even when a caller supplies a different source reference.

## Consequences

Preview cannot age into the future, a fee cannot be dated before the contractual threshold or the
confirmation that created it, and an auditor can follow the fee directly to its rent obligation.
Rent charges without the canonical period-and-lease source reference are not eligible for the bulk
run until they can be linked explicitly. The oldest-charge-first allocation is confined to deciding
whether that rent obligation remains open; it does not alter the existing delinquency-aging report.

## Revisit trigger

Revisit when the product supports explicitly backdated assessments, non-monthly rent obligations,
or manual rent charges that need to participate in the bulk late-fee run without a canonical rent
source reference.
