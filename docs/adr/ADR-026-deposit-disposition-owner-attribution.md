# ADR-026: A deposit disposition carries its collection's owner attribution (invariant I7)

- **Status:** Accepted
- **Date:** 2026-07-31
- **Deciders:** Engineering

## Context

A security deposit is owner-attributed on purpose: `DepositCollected` and the balance-forward opening
import both credit `security_deposits_held` with property + owner + tenant, and the owner-facing
deposit figure (`GetOwnerBalances`, owner balances, the dashboard) is a sum of that liability
**grouped by owner**. The two dispositions — `DepositApplied` and `RefundIssued` — debited the same
account with a tenant dimension but **no owner**.

Every per-tenant read therefore stayed correct (the deposit register, the tenant ledger, and invariant
I4 all net a fully disposed deposit to zero), while the owner-attributed column only ever went **up**:
an owner who had ever had a deposit applied or refunded was overstated forever. WP-13 found it against
the scenario org, where one owner's held deposits read 5,220.00 against a true 3,820.00.

Three forces shape the fix:

- The existing invariants could not see it. I4 aggregates the held liability **per tenant**, so an
  asymmetric collect/release pair is invisible to it — the tenant total is zero, and the error lives
  entirely in how that zero is split across owner buckets.
- "Always attribute a deposit line to an owner" is not the rule. A tenant **prepayment** is collected
  with no owner dimension at all (it is money held against the tenant's future rent, not an owner's
  position), and `GetOwnerBalances` deliberately drops it.
- The journal is append-only, so a template fix repairs the future, not the past.

## Decision

**A line that releases a held subledger position carries the same dimensions the line that created it
carried.** Concretely:

- `DepositApplied` and `RefundIssued(Source = Deposits)` pass `PropertyId`/`OwnerId` on the
  `security_deposits_held` debit, mirroring the collecting credit.
- `RefundIssued` gains optional trailing `PropertyId`/`OwnerId` applied **only** for
  `RefundSource.Deposits`. A `RefundSource.Prepayments` refund leaves both null, matching how the
  prepayment was collected. This is symmetry, not blanket attribution: inventing an owner on a
  prepayment refund would be as wrong as dropping one on a deposit refund.
- Callers outside the ledger command surface resolve the dimensions the same way the commands do,
  through `ITenantPostingDimensions` ([ADR-010](ADR-010-ledger-write-command-surface-and-actor-attribution.md)),
  never by guessing.

**A new core invariant, I7, backstops it at runtime:** the held security deposit stays **≥ 0 per
`(tenant, owner)` bucket** on the cash-inclusive bases, checked by `CheckCoreAsync` and the
`check-invariants` verb. It takes the id I7 because the canonical set runs I1–I6.

I7 is deliberately shaped as a **per-bucket floor** rather than "every deposit line carries an owner."
The demo fixture holds an intentional owner-null aggregate deposit position (the synthetic aggregate
for owners the prototype only summarises — [ADR-008](ADR-008-journal-dimension-fks-and-aggregates.md)),
and a blanket-attribution shape would fail it. An owner-null-throughout position never goes negative;
only an asymmetric pair does, because the release drives the unattributed bucket below zero while the
attributed one stays high.

## Consequences

- The owner deposit column now moves in both directions, and the defect class is visible two ways: I7
  fails on any org that has one, and a scenario golden tie-out asserts the owner-attributed deposit
  total equals what the deposit trust actually holds.
- Any future template touching a subledger liability has to reason about dimension symmetry, and any
  new deposit-refund caller must supply the dimensions. The trailing parameters are nullable for
  source compatibility, so omission compiles — I7 is the reason that is safe rather than silent.
- **History is not repaired.** A journal already holding a pre-fix disposition keeps its overstatement;
  there is no back-fill. The correction path is an ordinary linked reversal plus a re-post, like any
  other posted mistake. The scenario golden literal moved with the fix in the same change.

## Revisit trigger

If a held deposit ever legitimately needs to move **between** owner buckets — a property sold with the
deposit transferring to the new owner — I7's per-bucket floor is exactly the check that will fail.
Model that as an explicit transfer template that debits one bucket and credits the other in one
balanced entry, rather than relaxing the invariant.
