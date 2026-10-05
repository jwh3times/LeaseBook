# Processor fee and batched settlement specification

- **Audience:** Reviewers of issue #498 and implementers of the slice that follows it
- **Status:** Proposed model; nothing here is implemented, and live payments remain unapproved
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-05

## Evidence and boundary

[ADR-053](../adr/ADR-053-processor-fees-and-batched-settlement.md) owns the decisions. This document
owns the exact lines, the worked figures, the review reasons and the changes an implementation needs.
[ADR-046](../adr/ADR-046-simulated-payment-recognition.md) and the
[simulated payment specification](simulated-payment-spec.md) describe what is built today: gross,
fee-free bank evidence for one payment. [ADR-052](../adr/ADR-052-guarded-simulated-payment-return.md)
owns the guarded return. [Stripe research](../research/stripe-connect-payment-lifecycle.md) owns dated
provider facts.

`PaymentSettlementEvidenceTests` posts the lines specified here through the existing Accounting
engine and asserts the resulting balances and invariants. It is evidence for the model, not an
implementation of it.

Every rate and amount below is illustrative. None is a provider's pricing.

## Terms

| Term             | Meaning                                                                                       |
| ---------------- | --------------------------------------------------------------------------------------------- |
| Ledger amount    | What the tenant chooses to pay toward their ledger. The only amount the tenant is credited.   |
| Quoted fee       | The convenience fee shown to the tenant before they confirm.                                  |
| Charged amount   | Ledger amount plus quoted fee. What the tenant's bank or card is charged.                     |
| Processor fee    | What the processor actually keeps from the charged amount.                                    |
| Net amount       | Charged amount less processor fee. What the processor pays out for this item.                 |
| Fee difference   | Ledger amount less net amount. Positive is a shortfall; negative is a surplus; zero is clean. |
| Payout           | One processor transfer to the trust bank, covering one or more items.                         |
| Batch            | A payout together with the bank evidence for it.                                              |
| Funds in transit | Ledger amounts the processor has collected that no posted batch has yet answered for.         |

## Quoting the fee

The fee rule for a payment method is a rate, a fixed amount and an optional cap, held in
organization settings: `fee(charged) = min(cap, round(charged × rate + fixed))`, rounded to the cent,
half away from zero.

The charged amount is the smallest amount whose net is the ledger amount:
`charged − fee(charged) = ledger amount`. Such an amount always exists. Each extra cent charged raises
the net by one cent or by nothing, so no ledger amount is skipped. The evidence tests check this for
every amount to $200.00 and a sample to $3,000.00 under both rules below.

| Rule (illustrative)   | Ledger amount | Quoted fee | Charged amount |
| --------------------- | ------------- | ---------- | -------------- |
| 2.9% + $0.30, no cap  | $1,000.00     | $30.17     | $1,030.17      |
| 2.9% + $0.30, no cap  | $500.00       | $15.24     | $515.24        |
| 2.9% + $0.30, no cap  | $250.00       | $7.78      | $257.78        |
| 0.8%, capped at $5.00 | $1,000.00     | $5.00      | $1,005.00      |
| 0.8%, capped at $5.00 | $250.00       | $2.02      | $252.02        |

The tenant sees the ledger amount, the quoted fee and the charged amount before confirming. The
quoted fee is stored on the payment and never recomputed.

## What posts for one item

All lines are dated on the bank date of the batch and carry the trust bank that received the payout.
`P` is the ledger amount and `d` the fee difference.

| Case                | Lines                                                                                                                                  | Bank effect |
| ------------------- | -------------------------------------------------------------------------------------------------------------------------------------- | ----------- |
| Clean (`d = 0`)     | The existing `PaymentReceived` receipt for `P`: debit bank; credit receivable (accrual) and owner equity (cash); excess to prepayment. | `+P`        |
| Shortfall (`d > 0`) | The receipt for `P`, and a second entry: debit PM income `d`, credit bank `d`, basis both, bank dimension only.                        | `+P − d`    |
| Surplus (`d < 0`)   | The receipt for `P`, and a second entry: debit bank `−d`, credit PM income `−d`, basis both, bank dimension only.                      | `+P − d`    |
| Returned payment    | The guarded return of ADR-052: a linked reversal of that payment's receipt.                                                            | `−P`        |

In every case the bank effect equals the item's net amount, and the tenant is credited or debited
exactly `P`. A fee-difference entry carries no owner, property or tenant dimension, so it is
unreachable by an owner statement and changes no owner or tenant balance.

The shortfall entry has the lines of the existing bank-fee entry. The surplus entry has the lines of
the existing interest entry. Neither template is reused: an implementation adds one posting template
for a processor fee difference, so the journal names what happened.

**Shortfall guard.** A shortfall posts only if the PM's held fees in that bank, after it, are at or
above zero. Read under the posting lock. If not, the batch does not post
(`pm_fees_insufficient`). The existing bank-fee entry has no such guard; the evidence test
`A_shortfall_beyond_held_fees_posts_today…` shows the result — held fees at −$7.50, the bank $7.50
short of what the owner is owed, and every invariant green.

A processor's own return or dispute fee is a shortfall with no payment beside it: the same entry, the
same guard.

## What posts for a batch

A batch is one unit of work. It posts completely or not at all.

1. **Identify.** Every item in the processor's reconciliation for the payout maps to exactly one
   payment in this organization, in USD, for this trust bank. Otherwise: `settlement_incomplete`.
2. **Tie.** The items' net amounts sum to the bank evidence amount to the cent, and each item's
   charged amount equals the payment's stored charged amount. Otherwise: `settlement_untied`.
3. **Check for unsupported content.** A reserve, a hold, an instant or manual payout, a partial
   refund or a non-USD item: `unsupported_settlement`.
4. **Check for negative items.** If any item is a return, refund or dispute, stop:
   `settlement_requires_confirmation`. An administrator posts the batch with one action.
5. **Post.** Under the posting lock, post every item's lines and record one effect per item. A
   refused guard — the shortfall guard, any ADR-052 return guard, a locked period — rolls the whole
   batch back and records the refusal as the batch's reason.

A payout whose items net to a bank debit is a batch like any other, with a negative bank amount.
Nothing is posted from a processor's notice that a payout was paid; only bank evidence opens a batch.

### Worked batch

One payout, bank evidence $1,742.90, three items, card-shaped rule. Held PM fees in the bank before
the batch: $80.00.

| Item | Ledger amount | Quoted fee | Charged   | Processor fee | Net       | Fee difference           |
| ---- | ------------- | ---------- | --------- | ------------- | --------- | ------------------------ |
| 1    | $1,000.00     | $30.17     | $1,030.17 | $30.17        | $1,000.00 | $0.00                    |
| 2    | $500.00       | $15.24     | $515.24   | $22.74        | $492.50   | $7.50 shortfall          |
| 3    | $250.00       | $7.78      | $257.78   | $7.38         | $250.40   | $0.40 surplus            |
|      | $1,750.00     |            |           |               | $1,742.90 | $7.10 net cost to the PM |

Posted: three receipts totalling $1,750.00, one shortfall entry of $7.50 and one surplus entry of
$0.40. Bank book up $1,742.90, matching the deposit. Tenants credited $1,750.00. Held PM fees
$80.00 − $7.50 + $0.40 = $72.90. Owner equity up by the receivable portions of the receipts and by
nothing else.

### Worked batch with a return

A later payout: one new payment of $400.00, clean, and the return of an earlier $600.00 payment. Bank
evidence is a debit of $200.00. The batch waits for an administrator. On posting, the receipt adds
$400.00 and the guarded return removes $600.00, both on the bank date; the returned tenant owes the
$600.00 again. If the owner was paid out in between, the return guard refuses and neither line
posts. The evidence tests cover both outcomes.

## Funds in transit

Funds in transit for a tenant is the sum of the ledger amounts of that tenant's payments for which
the processor has reported the collection succeeded, no failure or return has been observed, and no
posted batch contains the payment. The organization figure is the sum over tenants.

It is a read over Payments' own records. It is never posted, never added to a tenant's balance and
never used as an input to a posting. The tenant sees "paid on {date}, on its way to the bank" beside
a balance that still includes the amount. Staff see the organization total and each tenant's figure.

## Late fees and payments in transit

The payment record keeps the date the processor reported the collection succeeded: the _paid date_.
The journal date of the receipt remains the bank date.

The late-fee run preview reads, through a port Operations owns, the tenants with funds in transit and
each one's paid date and amount. It marks those tenants and leaves them unselected by default. Staff
may select them. The run posts the same fee for a selected tenant as it would otherwise: this rule
changes selection, never an amount. A payment that later fails leaves transit, and the next run
treats the tenant as it would any other.

## Reconciliation

Every entry posted from a batch has a source reference that begins with the payout's reference:
`payout:{payout id}:{item}` for a receipt or return, and `payout:{payout id}:{item}:fee` for a fee
difference. A statement line for the payout matches the group of bank lines whose entries share that
payout reference when their signed sum equals the line's amount. The group clears together or not at
all. A statement line never clears part of a group, and a bank line that belongs to a group is never
offered as a one-to-one match.

## Review reasons

| Reason                             | Meaning                                                             | Way out                                    |
| ---------------------------------- | ------------------------------------------------------------------- | ------------------------------------------ |
| `settlement_incomplete`            | An item maps to no payment, or to more than one.                    | More evidence, or close with a note        |
| `settlement_untied`                | Items do not sum to the bank amount, or a charged amount differs.   | Corrected evidence, or close with a note   |
| `unsupported_settlement`           | A reserve, hold, instant or manual payout, partial refund, non-USD. | Close with a note after correcting by hand |
| `settlement_requires_confirmation` | The batch contains a return, refund or dispute.                     | An administrator posts the batch           |
| `pm_fees_insufficient`             | A shortfall would take held PM fees below zero.                     | Fund held fees, then post; or close        |
| `return_*`                         | A return in the batch failed an ADR-052 guard.                      | As ADR-052                                 |

Closing a batch with a note posts nothing, as closing a payment review does today.

## What an implementation needs

This list is the input to the implementation issue. None of it is built.

- **Organization settings:** per payment method, a rate, a fixed amount and an optional cap, with
  validation and an audit trail.
- **Payment record:** the ledger amount, quoted fee, charged amount, method and paid date. Today the
  record holds one amount.
- **Batch record:** a payout with its bank evidence, its items, its status and reason, and one effect
  per posted item. Today an effect belongs to a single payment.
- **Accounting:** one posting template for a processor fee difference, in both directions, with the
  shortfall guard; a narrow port for Payments to post a whole batch in one transaction.
- **Simulator:** bank evidence that names a payout with several items, each with gross, fee and net;
  the CLI and callback shapes to drive a shortfall, a surplus, a return inside a payout and an untied
  batch.
- **Operations:** a consumer-owned port for tenants with funds in transit, and the preview flag.
- **Banking:** group matching of one statement line against a payout's bank lines.
- **Staff and tenant surfaces:** the fee quote before confirm, the receipt showing ledger amount and
  fee, funds in transit, and the batch review with its one posting action.

## Provisional assumptions

The model rests on these. Each is unverified and needs legal or processor confirmation before real
money moves.

1. A property manager may charge a residential tenant a convenience fee, disclosed as described.
2. The processor's retained fee is not trust money that must pass through or be recorded in the
   trust account.
3. The charge is a direct charge owned by the property manager's processor account and paid out to
   its trust bank; payouts can be restricted to that account.
4. A shortfall may be covered from earned fees still held in the trust account.
5. A tenant's paid date may be used when deciding whether to charge a late fee.
6. The processor publishes, for each payout, a complete list of items with gross, fee and net.

Simulation conventions cannot settle them, and no configuration enables live processing.
