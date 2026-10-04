# ADR-052: A returned simulated payment posts only as a guarded full reversal

- **Status:** Accepted
- **Date:** 2026-10-04
- **Deciders:** Jerry Holland

## Context

[ADR-046](ADR-046-simulated-payment-recognition.md) records a return, refund or dispute that arrives
after a receipt has posted as a review-required fact and preserves the receipt. That left the payment
in `NeedsReview` with no action: staff could neither post the return nor mark the review done.

The unrestricted reversal service is not a safe answer. A receipt's credit may have been spent since
it posted. Excess that became prepaid credit may have been applied to a later charge, and cash equity
the receipt gave an owner may have been disbursed. A mirror entry still balances and the trust equation
still holds, but a liability or an owner balance goes negative. The
[evidence tests](../../tests/LeaseBook.Tests.Accounting/PaymentLifecycleEvidenceTests.cs) demonstrate
the prepayment case.

## Decision

**Only a full return is posted, and only as the receipt's linked reversal.** `ReturnTenantPayment` in
Accounting mirrors every receipt line in the same basis and dimensions, linked to the receipt and dated
on the bank's evidenced return date. It never uses the receipt date or today. A partial return, a
refund and a dispute are not posted.

**Accounting guards the reversal under the posting lock.** It refuses, and posts nothing, when the
reversal would leave any of these below zero:

- the tenant's prepaid credit in the bank the receipt went into (`return_prepayment_consumed`);
- the owner's cash equity attributed to that bank, or the owner's total cash equity
  (`return_owner_funds_disbursed`). Both are read: the per-bank figure stops equity held in another
  bank from covering the reversal, and the total stops a payout recorded without a bank dimension
  from being missed.

**The guards read the lowest balance on or after the return date, not today's.** The reversal is
backdated to the bank's date, so it lowers every balance from that day on. Money that arrived later
must not hide a balance the reversal would have driven negative in between, which a statement for
that period would then show.

A return dated before its receipt is refused (`return_precedes_receipt`). A return dated in a closed
period or a reconciled bank month is refused by the posting service like any other entry
(`return_period_locked`). No date is substituted and no period is unlocked. A receipt that is already
reversed answers `already_reversed` before the guards run, because its own reversal emptied the
balances they read.

**A person posts it.** Return evidence alone never posts. A PMAdmin chooses "Post return" on the
payment in review. Payments reaches the command through `IPaymentLedger.ReturnSettledReceiptAsync`
([ADR-007](ADR-007-cross-module-read-contracts.md)), and one `Return` effect per payment makes a
repeat a no-op. The reversal, the effect and the `Returned` status commit in one transaction.

**A refusal is durable and leaves a way out.** A refused return leaves the payment in `NeedsReview`
under the `return_*` reason that names what stopped it. A PMAdmin can then "Close review" with a
required note. Closing posts nothing, keeps every observation, and is recorded by the ordinary
attributed audit of the payment row. Staff correct the books with the existing ledger tools. An
observation that arrives after a closure reopens the review. A technical failure is the one review
that cannot be closed: its way out is the existing retry, and closing it would leave an operation that
still posts its receipt when evidence next arrives.

**A posted return answers only the evidence it was posted from.** A refund, a dispute, or a return
that disagrees with it arriving afterwards puts the payment back in review (`evidence_after_return`).
It can then be closed, not posted again.

**The tenant is not told why.** The tenant portal reports every return-related reason as "a return is
under review". Why a return was refused describes the owner's position and the ledger, which is staff
knowledge.

**No returned-payment fee.** Whether a tenant is charged for a return, how much, and whose income it
is are money policy. They belong in organization settings and a later decision, not in this command.

## Consequences

Every simulated payment in review now has an action. The automatic path stays closed: ADR-046's
revisit trigger for "automatic return" is not fired, because nothing posts without a named actor.

The reversal's owner-facing description is `Void — Simulated tenant payment`, as for every reversal.
A returned payment is therefore worded as a void on the tenant ledger and the owner statement.

The closing note lives on the payment row. The tenant portal never returns it, but the tenant
persona's row grant on its own payments ([ADR-048](ADR-048-per-persona-row-level-security.md)) covers
the row that holds it. The note is written for staff and should not contain anything a tenant must
not see.

The owner-equity guard reads the per-bank attribution that an existing issue reports as unreliable for
a multi-trust organization. The total-equity read bounds the damage, and the simulation fixture has
one trust bank.

This policy applies to the isolated simulation only. It sets no real-money return policy.

## Revisit trigger

Revisit before posting a return automatically, posting a partial return, refund or dispute, charging a
returned-payment fee, moving the closing note behind a staff-only table, or using this command for a
live processor.
