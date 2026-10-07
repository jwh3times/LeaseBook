# ADR-054: A Stripe sandbox adapter proves the seam; a payout waits for bank evidence

- **Status:** Proposed
- **Date:** 2026-10-07
- **Deciders:** Jerry Holland

## Context

The payment simulation settles tenant payments through one processor seam, `IPaymentProcessor`, with
the simulator as its only implementation.
[ADR-046](ADR-046-simulated-payment-recognition.md) recognizes a payment only from bank evidence,
[ADR-052](ADR-052-guarded-simulated-payment-return.md) posts a return only when an administrator
asks, and [ADR-053](ADR-053-processor-fees-and-batched-settlement.md) posts a payout as one tied
batch. Each names a real processor adapter as a trigger to revisit.

A second implementation is the test of whether that seam and those rules hold against a processor
nobody here wrote. The [provider research](../research/stripe-connect-payment-lifecycle.md) says
where they will be strained. A Stripe event does not carry everything the simulator's notices carry.
An ACH payment can fail after it has succeeded. And Stripe never states that money reached a bank: a
payout marked `paid` can later become `failed`.

No real money may move. Issue #513 holds the scope and the order of work.

## Decision

**The adapter talks to a Stripe sandbox, and only from a maintainer's machine.** It is a server-side
proof with no payment form. It uses Stripe's documented test payment methods, for card and for ACH.
Continuous integration never calls Stripe; it replays recorded payloads.

**The charge is a direct charge on a connected account.** Each fixture organization is bound to its
own connected account, created so that the connected account pays Stripe's fees. The tenant is
charged the ledger amount plus the quoted convenience fee, with no platform application fee. The gap
between Stripe's actual fee and the quoted fee posts through the existing `ProcessorFeeDifference`
template and its shortfall guard. No posting template and no money path is added.

**A third mode, `StripeSandbox`, sits behind an executable barrier.** It is admitted only in
Development, with a test-mode secret key, the fixture signing key, and every fixture bound to a
connected account. A live key is refused by its prefix, and an event marked live is rejected. `Live`
stays refused. A host runs one mode, so one processor is registered per host. No capability is added
([ADR-028](ADR-028-platform-capability-model.md)): the mode and the fixture binding already decide
reachability, and both are operator configuration.

**One seam, widened.** A request to the processor carries what is to be collected and when the
operation was created. A callback is first proved authentic with no I/O, and only then read, which
may fetch from the processor. Reading answers one of three ways: the content, not this host's to act
on, or unreadable. The generation and bank on what is read come from the host's own binding for the
account, never from the event. The Stripe library is referenced from the host only, in one adapter
folder, and an architecture test fails the build on a Stripe type in any module.

**Stripe supplies a payout's lines; the bank supplies the proof.** A Stripe payout is stored with
its lines and held, shown as waiting for the bank. Bank evidence arrives separately, from a
fixture-only command that names the payout, the bank amount and the bank date. A bank amount equal to
Stripe's net posts the whole payout or nothing; any other amount goes to review. This keeps ADR-046's
recognition rule and adds a held state to ADR-053's batch, which this ADR will amend when accepted.
Stripe delivers payouts only: single-payment bank evidence stays a simulator path.

**The adapter never decides what a payout covers.** Membership comes only from Stripe's own list for
an automatic payout. A manual payout has no such list, so the adapter neither creates one nor
allocates charges to one.

**Everything unknown goes to a person.** An ACH return after success reaches the adapter as a
dispute and becomes the returned-payment review of ADR-052. A payout that fails, a refund, a card
dispute and a balance line of an unknown type are stored for review and never posted. Issuing
refunds, contesting disputes, reserves and instant payouts are out of scope.

**An unknown result is found, never repeated.** Stripe may forget an idempotency key after a day. The
adapter then lists the connected account's payment intents over the operation's creation window and
matches the operation id it stored in metadata. It does not use Stripe's search, which can lag. If
nothing is found the payment goes to review; it is not submitted again.

**Nothing that identifies a tenant is sent.** A charge carries the amount, currency, a test payment
method named by the fixture binding, and the operation id and fixture generation as metadata. No
customer object is created. ACH is confirmed with an offline mandate acceptance; collecting a real
mandate belongs with a payment form.

**Recorded payloads are committed scrubbed.** A probe
([runbook](../runbooks/stripe-sandbox-probe.md)) records what the sandbox sends. Before a payload is
committed, every identifier is replaced with a synthetic one and anything that identifies a person,
a bank account or a device is removed. A test fails on any committed payload that still holds a real
identifier, an address or a URL.

## Consequences

The seam is proved by a processor that behaves in ways the simulator was not written to imitate, at
the cost of a second implementation to keep working. The transaction and recovery protocol of the
[simulated-payment specification](../payments/simulated-payment-spec.md) applies to both without a
separate path.

A payout cannot post from Stripe's word alone. In the sandbox, a person supplies the bank evidence
with a command; a later release needs a real source for it, such as a statement line.

The adapter can be exercised only by someone holding a sandbox. Replayed payloads keep the suite
hermetic, and they go stale silently if Stripe changes a shape. The probe can be rerun to refresh
them.

These provider facts are not settled by Stripe's documentation. The probe answers them, and the
decisions above are provisional on its findings:

- whether a sandbox produces automatic payouts, on what timing, and whether it reports their
  reconciliation as complete;
- whether the documented test payment methods can be used directly on a connected account;
- whether the ACH test payment methods need a small test deposit verified first, and how long a test ACH
  payment stays in processing;
- which dispute reason the ACH dispute test method produces, how long after success it arrives, and
  which dispute event is delivered;
- whether the fees a sandbox reports are realistic;
- whether Connect events forwarded by the Stripe CLI are signed with the same secret as account
  events.

If a sandbox produces no automatic payout at all, the payout mapping is built from the documented
shapes and recorded here as unproven.

The provisional assumptions of ADR-053 stay open. This ADR verifies none of them, and nothing here
is approval to move real money.

## Revisit trigger

Revisit when the probe's findings contradict a decision above; before any payment form or real
mandate is added; before a payout posts without separate bank evidence; before the adapter sends
anything that identifies a tenant; and before any mode that can reach a live Stripe account exists.
