# ADR-011: An application may not exceed the open receivable (warn + block)

- **Status:** Accepted
- **Date:** 2026-06-13
- **Deciders:** Engineering

## Context

A tenant payment that exceeds what is owed has a well-defined home: the `PaymentReceived` template
auto-splits the excess into a prepayment liability (P31), so a receivable is never driven negative. An
**application** of held funds — `DepositApplied(Target = AgainstCharges)` or `PrepaymentApplied` — has
no such overflow path. The M1 review (carried into the m1/m2 retros as a "the M3 composer owns this"
finding) noted that the engine guarded these applications against the *held liability* (you cannot apply
more deposit than is held) but **not** against the *open receivable*. So an over-application would clear
the receivable past zero and silently leave it negative — a figure on the tenant ledger that disagrees
with reality. No existing invariant caught this: I4 covers liabilities (held ≥ 0), not receivable sign.

The options considered: (a) clamp the application to the open receivable and route any excess somewhere;
(b) let it post and report a negative receivable; (c) reject the over-application and make the user lower
the amount. Clamping hides intent and needs an arbitrary destination for the excess; allowing a negative
receivable is the bug. The product is correct trust accounting, so the engine should refuse what it
cannot represent honestly.

## Decision

**The engine rejects an application that would exceed the tenant's open receivable.**

- A new typed `InsufficientReceivableException` (`code = "insufficient_receivable"`, HTTP 409) is thrown
  by `AccountingEventService` when a `DepositApplied(Target = AgainstCharges)` **or** a
  `PrepaymentApplied` would post more than `max(openReceivable, 0)` for the tenant. The check reads the
  receivable under the per-org advisory lock the guarded events already hold (P31), alongside the
  existing held-liability check — both guards can fire, and each message names the limit it hit.
- `DepositApplied(Target = ToOwnerIncome)` is **deliberately not guarded**: damages legitimately exceed
  any rent owed, and that path recognizes owner income directly rather than clearing a receivable.
- The guard is **purely additive**: valid postings (the seeder, the golden replay, the property suites)
  are unchanged, and the M1 invariants stay green. The composer/apply modal surfaces the rejection in
  place — an inline warning naming the amount owed, with the modal kept open so the user lowers the
  amount — rather than treating it as a hard error.

## Consequences

- A tenant's receivable can never be driven negative by an application; the only way to a credit balance
  is the legitimate prepayment path. The tenant ledger stays trustworthy.
- The over-application is caught at the engine, not the UI, so every caller (composer, future bulk runs,
  imports) inherits the rule — the UI only has to render the message.
- Two pre-existing engine tests that applied against charges with *no* prior receivable (they exercised
  line structure, not a realistic flow) now post a rent charge first; they assert the same line set
  under a valid precondition.

## Revisit trigger

If a product flow ever needs to apply held funds beyond the open receivable on purpose (e.g. converting
an over-applied deposit into a prepayment), add an explicit target for that intent rather than relaxing
this guard. Revisit if the receivable read under the advisory lock becomes a contention hotspot at scale
(it is a single indexed aggregate today).
