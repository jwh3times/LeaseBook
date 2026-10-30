# ADR-011: An application may not exceed the open receivable (warn + block)

- **Status:** Accepted
- **Date:** 2026-06-13
- **Deciders:** Engineering

## Context

A tenant payment that exceeds what is owed has a well-defined home: the `PaymentReceived` template
auto-splits the excess into a prepayment liability (P31), so a receivable is never driven negative. An
**application** of held funds — `DepositApplied(Target = AgainstCharges)` or `PrepaymentApplied` — has
no such overflow path. The M1 review (carried into the m1/m2 retros as a "the M3 composer owns this"
finding) noted that the engine guarded these applications against the _held liability_ (you cannot apply
more deposit than is held) but **not** against the _open receivable_. So an over-application would clear
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
- Two pre-existing engine tests that applied against charges with _no_ prior receivable (they exercised
  line structure, not a realistic flow) now post a rent charge first; they assert the same line set
  under a valid precondition.

## Revisit trigger

If a product flow ever needs to apply held funds beyond the open receivable on purpose (e.g. converting
an over-applied deposit into a prepayment), add an explicit target for that intent rather than relaxing
this guard. Revisit if the receivable read under the advisory lock becomes a contention hotspot at scale
(it is a single indexed aggregate today).

## Addendum — a credit is bounded the same way, unless an administrator allows it (2026-10-29, #652)

A goodwill credit (`CreditIssued`) has no excess path either: it moves no cash, so a credit above the
open receivable left the tenant owing less than nothing with no money held behind it, and lowered the
owner's accrued income by more than the tenant owed. The engine checked only that the amount was
positive.

- `CreditIssued` takes the posting lock, reads the tenant's receivable and refuses an amount above
  `max(openReceivable, 0)` with `InsufficientReceivableException` (`ReceivableSource.Credit`, 409
  `insufficient_receivable`). The guard sits in the posting, so every caller inherits it, including the
  seeders that post the event directly.
- An excess posts only with an explicit `AllowUnappliedCredit` flag, carried on `IssueCredit`, the event
  and the request body. **Only a PMAdmin may set it**: the credits route refuses the flag from anyone else
  with 403 `unapplied_credit_requires_admin` before anything is read. A credit the flag let past the
  receivable posts with `event_subtype = 'unapplied'`, so the allowance stays visible on the entry and in
  its audit row; a flag the credit did not need leaves no mark. The lines and amounts are unchanged.
- The tenant ledger read reports `Owed`, the receivable never below zero, which the credit form shows and
  checks against. It is not the ledger balance: a held prepayment lowers the balance without lowering what
  a credit may draw on.
- **Rejected:** converting the excess into a prepayment. A prepayment is a trust-equation term, so without
  a bank debit the equation breaks, and with an owner-equity debit the credit becomes an owner-funded
  payment to the tenant, a different event needing the owner's authority. **Rejected:** a hard cap with
  no allowance. The demo dataset's 85.00 goodwill credit lands after February is paid in full, against
  nothing owed; the demo seed now passes the flag, so no golden figure moves.
- No absolute per-organization ceiling. If one is wanted, it belongs in `OrgSettings`.

This narrows the Consequences above: a receivable now goes below zero only through the prepayment path or
an administrator's explicitly allowed credit.
