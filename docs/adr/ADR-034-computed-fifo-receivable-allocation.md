# ADR-034: Computed FIFO receivable allocation and charge due dates

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Jerry Holland
- **Amends:** [ADR-033](ADR-033-late-fee-eligibility-and-rent-obligation-link.md)

## Context

Delinquency aging netted tenant-receivable debits and credits in the journal dates where they
posted. The total balance was right, but a payment could appear to satisfy a newer charge while a
paid older charge remained overdue, and individual buckets could become negative. Journal date is
also not a rent obligation's contractual due date. ADR-033 introduced FIFO only for deciding
whether a rent obligation remained open and explicitly left the aging report unchanged.

## Decision

Every new charge records a contractual `due_date` on its journal header. The rent run carries the
effective lease due date separately from its first-of-month accounting date; manual charges default
their due date to their accounting date. Historical rows without the field fall back to
`entry_date`.

As-of receivable reads ignore a linked original/reversal pair once the reversal is effective, order
positive charges by due date and then stable journal order (`posted_at`, `id`), and allocate tenant
payments and general credits to that order. Aging contains only each charge's remaining amount.
Excess general credit is returned as `unapplied_credit`, separate from the gross open-charge total;
payment excess remains a prepayment liability until explicitly applied. Rent-obligation lookup uses
the same ordering and participation rules for late-fee assessment.

Allocation remains computed read-model state. Phase 1 adds no allocation table and no mutable link
from a payment to a charge.

## Consequences

A paid charge cannot remain in an overdue bucket, buckets cannot go negative, and fee assessment and
aging agree about which rent obligation is open. The journal schema gains one nullable date, and
analytical reads do more ordered work. Historical charges retain their observable age through the
entry-date fallback because their original contractual due date cannot be reconstructed safely.

## Revisit trigger

Revisit when operators need explicit charge-level allocation, a payment must be split by a rule
other than FIFO, or allocation identity must survive independently of journal replay.
