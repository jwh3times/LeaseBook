# ADR-053: Processor fees stay out of the trust journal; a payout posts as one tied batch

- **Status:** Accepted
- **Date:** 2026-10-05
- **Deciders:** Jerry Holland

## Context

[ADR-046](ADR-046-simulated-payment-recognition.md) recognizes a simulated tenant payment only from
explicit bank evidence, and only when that evidence is gross, fee-free and for one payment. It names
fees, net settlement and fee allocation as triggers to revisit. A real processor meets none of those
limits. It keeps a fee from each payment, pays out many payments in one bank deposit, and takes
returns out of a later payout. The
[provider research](../research/stripe-connect-payment-lifecycle.md) records the sources.

Two facts bound the design. The trust equation says a trust bank's book balance equals owner equity
plus deposit liabilities plus held PM fees, and it has no term for money at a processor. And the
[lifecycle evidence tests](../../tests/LeaseBook.Tests.Accounting/PaymentLifecycleEvidenceTests.cs)
show that neither a gross nor a net receipt can represent a collection with a withheld fee: one
overstates the bank and the other leaves the tenant owing.

This ADR is accepted for the isolated simulation, as ADR-046 was, and its provisional assumptions stay open: none is verified, and none is approval to move real money. In the simulation, its fee rules, fee quote and the fee on a payment are built, and a payment whose processor fee equals the quoted fee settles. Payout batches are built too: stored, checked and posted completely or not at all, with an administrator posting any payout that contains a return. Evidence arrives through a signed callback or the fixture command. Funds in transit and the late-fee caution are built. So is group reconciliation: a statement line matches a payout's bank lines as a whole, and no clearance takes part of a payout. Staff review payouts on the Operations page and an administrator sets the fee rules on the Settings page. The single-payment evidence of ADR-046 remains valid for a clean payment; the specification says how the two relate.

## Decision

**Recognition stays at bank evidence.** No clearing account is added and the trust equation does not
change. Money a processor has collected but not yet paid out is _funds in transit_: a figure Payments
reads from its own records and shows to staff and to the tenant. It is never a journal balance.

**The tenant pays the processor's fee, and the fee is not in the journal.** The tenant chooses an
amount to pay toward their ledger. The charge is that amount plus a disclosed convenience fee, grossed
up so that the amount left after the processor's fee is the ledger amount to the cent. The fee is kept
on the payment record and shown on the tenant's receipt. It never enters the trust bank, so the trust
journal records only the ledger amount. Fee rates are organization settings per payment method.

**The tenant is always credited the ledger amount.** When the processor keeps a different fee from
the one quoted, the difference is the property manager's:

- a _shortfall_ is borne by the PM's held fees in that bank, in the shape of the existing bank-fee
  entry, and only while held fees stay at or above zero;
- a _surplus_ becomes held PM fees and leaves through the existing sweep.

No owner balance and no tenant balance changes because of a fee.

**A payout posts as one batch, or not at all.** Bank evidence names a payout, and the processor's
reconciliation names its items. Every item must be identified and the items must sum to the bank
amount to the cent. Then each payment's receipt posts on the bank date, with any fee difference
beside it. Otherwise nothing in the batch posts and the batch goes to review.

**A batch with a negative item needs a person.** A return, refund or dispute inside a payout stops
automatic posting. An administrator posts the whole batch with one action, each return in it passing
the guards of [ADR-052](ADR-052-guarded-simulated-payment-return.md) on the bank date. If any guard
refuses, nothing in the batch posts. A processor's own return or dispute fee is a shortfall. A payout
whose items net to a bank debit follows the same rule.

**Late fees do not ignore a payment in transit.** The journal stays unaware of it. The payment record
keeps the date the tenant paid, and the late-fee run preview flags a tenant with a payment in transit
and leaves them unselected by default. This changes who is selected, never an amount.

**One payout reconciles as one statement line.** Every journal bank line posted from a batch carries
the payout's reference. Statement matching must clear that whole group against one statement line when
the group sums to it, and never part of a group.

The [specification](../payments/fee-and-settlement-spec.md) owns the exact lines, the worked figures,
the review reasons and the list of changes an implementation needs.

## Consequences

The trust bank book continues to hold exactly what the bank holds. A tenant who pays by a slow method
shows as owing until the bank shows the deposit, and the in-transit figure and the late-fee flag are
what make that tolerable.

A shortfall the PM's held fees cannot cover does not post. The
[settlement evidence tests](../../tests/LeaseBook.Tests.Accounting/PaymentSettlementEvidenceTests.cs)
show why: the bank-fee entry has no balance guard, a rule [ADR-014](ADR-014-reconciliation-engine-and-lock.md) deliberately left to procedure, and an uncovered shortfall posts with every
invariant green while held fees go negative and the bank holds less than owners are owed.

The following are assumptions, not findings. Each needs legal or processor confirmation before any
real money moves, and the model must be revisited if one is wrong:

- a property manager may charge a tenant a convenience fee, with the disclosure assumed here;
- money the processor retains as its fee, before any deposit, need not pass through or be recorded
  in the trust account;
- the charge is a direct charge owned by the property manager's own processor account, paid out to
  its trust bank, so the platform never holds tenant money;
- covering a shortfall from earned fees still held in the trust account is acceptable;
- a tenant's payment date for late-fee purposes may differ from the date the bank received it.

Processor reserves and holds, instant payouts, manual payouts, partial refunds and currencies other
than USD are outside the model. Evidence of any of them sends the batch to review.

## Revisit trigger

Revisit before implementing a live processor adapter; if any assumption above is corrected; before
recognizing a payment ahead of bank evidence; before any fee is charged to an owner; and before
posting any return in a batch without a person.
