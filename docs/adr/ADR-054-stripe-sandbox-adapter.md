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
batch. ADR-046 names a Stripe adapter as a trigger to revisit; the other two name a live processor.

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
a bank account or a device is removed. An account is reduced to the few fields the adapter reads. The
copy is then searched for every identifier and account name the run recorded, and nothing is written
if one is found. A test fails on any committed payload that still holds a real identifier, an address
or a URL.

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

These provider facts were not settled by Stripe's documentation. The probe ran against a sandbox on
2026-10-08, at API version `2026-09-30.endive`, and answered all but the first and half of the last. None of its answers
contradicts a decision above. The recorded payloads are under `tests/fixtures/stripe/`.

- **Does a sandbox produce automatic payouts, on what timing, and does it report their
  reconciliation as complete?** Not answered yet. No payout existed on the day of the run; the
  probe's `payouts` step is run on a later day. Until then the payout mapping rests on the
  documented shapes and is unproven.
- **Can the documented test payment methods be used directly on a connected account?** Yes, for card
  and for ACH, as direct charges with no customer object.
- **Do the ACH test payment methods need a small test deposit verified first, and how long does a
  test ACH payment stay in processing?** No verification was asked for. A payment left `processing`
  after about half a minute.
- **Which dispute does the ACH dispute test method produce, and when?** `debit_not_authorized`, with
  the dispute already `lost`. It arrived between three and ten minutes after the payment succeeded,
  as `charge.dispute.created`, then `charge.dispute.funds_withdrawn`, then `charge.dispute.closed`.
- **Are the fees a sandbox reports realistic?** Yes. Card is 2.9% plus $0.30 and ACH is 0.8%,
  reported as one `stripe_fee` line and taken from the connected account.
- **Are Connect events forwarded by the Stripe CLI signed with the same secret as account events?**
  Every Connect delivery verified with the secret `stripe listen` prints. No account event was
  delivered, because all activity was on the connected account, so the account half is not shown.

The run also found things nobody had asked about. The adapter in step 4 has to allow for each:

- **A card charge succeeds before its fee exists.** The balance transaction is attached a few
  seconds later, with a `charge.updated` event. The adapter must not read a fee when a payment
  succeeds. A failed ACH debit also carries a fee.
- **Stripe refuses `payment_method_types` at this API version.** What a connected account accepts is
  decided by its payment method settings. A charge is confirmed with automatic payment methods and
  redirects ruled out. ACH debit has to be turned on in those settings; an active capability is not
  enough.
- **Stripe refuses to create a connected account through the first-generation Accounts API for a new
  platform.** The account is created through Accounts v2, where "the connected account pays Stripe's
  fees" is written as Stripe collecting its fees from the account. Read through the first-generation
  API with the platform's key, such an account reports the fee payer as `account`, which is what the
  adapter checks.
- **A connected account with its own dashboard has its own keys.** One of them can read that account
  and still is not the platform's key. The barrier must refuse a key whose own account is the
  connected account.
- **Recovery works as decided.** Repeating a request with its idempotency key returns the first
  payment; the same key with another amount is refused. Listing the creation window finds a payment
  by its operation id. Stripe's search also found it at once, and the adapter still does not use it.
- **Every event names the connected account and the API version**, delivered or fetched.

**Implementation note, 2026-10-08 (submit and lookup).** The adapter now submits a charge and finds
it again; at that date it read no event and no payout, so nothing it collected posted. Three points the
decisions above leave open were settled in code. None changes a decision, and each is open to
revision until this ADR is accepted:

- **Which Stripe states count as accepted.** Only `succeeded`, `processing` and
  `requires_payment_method`: collected, on its way, or refused, the last of which arrives as an
  event. A payment waiting on a payer, a confirmation or a capture, cancelled, or in a state nobody
  has named would wait for ever, so it goes to review.
- **A key or permission Stripe refuses is a technical failure, not a review.** It charged nothing,
  and mending the key makes the same request valid, so it is retried like a failed connection. A
  request Stripe will not take however often it is sent still goes to a person. An operation more
  than 23 hours old is never submitted, an hour inside the day Stripe keeps an idempotency key.
- **The barrier asks Stripe whose key it holds.** At startup, and before a foreground worker step, a
  key whose own account is a fixture's connected account is refused, and so is a fixture whose
  account does not pay its own Stripe fees. The optional test payment methods a fixture may name are
  admitted only in the shape of Stripe's documented test tokens.

**Implementation note, 2026-10-09 (webhooks).** A sandbox host now accepts Stripe's signed
deliveries at its own callback route and reads `payment_intent.processing`, `payment_intent.succeeded`
and `payment_intent.payment_failed` as the existing Processing, Succeeded and Failed facts. A success
sets the paid date and the payment stays in Processing; a failure ends it. None of these facts is
bank evidence, and it still reads no payout, so nothing it collects posts. It was run against a
sandbox on 2026-10-09 through the Stripe CLI's forwarding: card and ACH payments succeeded, declined
cards and an ACH debit with insufficient funds ended as failed, and earlier probe payments on the
same account produced no facts. The probe's finding that no account event was delivered still
stands. These points were settled in code, on the same terms as the note above:

- **Only this fixture's generation counts.** A connected account also holds payments of the probe and
  of earlier fixture generations. An event counts only when its payment's metadata carries the
  generation of the fixture bound to that account; any other is acknowledged and dropped.
- **Reading an event needs no fetch.** A snapshot event carries the payment it is about, so the
  adapter asks Stripe nothing to read one. An event not marked as test mode is refused.
- **Events not yet handled are acknowledged and dropped.** Stripe is not asked to redeliver them, and
  they are not stored. Returns, disputes and payouts wait for later steps.
- **The signature is verified by the Stripe library, on the injected clock.** The adapter first
  bounds the body and the header and refuses a header that is not Stripe's shape; the library then
  judges the signature with its default tolerance of 300 seconds. The event itself is read member by
  member and not through the library's event types, which reject a shape or version they do not
  expect.
- **A delivery and the worker share a lock on the payment's reference.** An operation first takes its
  reference when the worker stores the result of a charge, and Stripe can deliver an event for that
  reference at the same moment. Without the lock each could miss the other, leaving a fact stored and
  never judged. The lock is in the shared engine, so the simulator takes it too.
- **A stored reference is retrieved, not searched for.** Once an operation holds Stripe's reference,
  lookup retrieves that payment and holds it to the checks a listed payment is held to. A reference
  Stripe has no payment for goes to review.

One question was left open by that note: after Stripe accepts a payment the worker waits for an
event, and if the event that ends a payment is lost, the payment stays in Processing with no staff
control to retry or close it. Stripe redelivers to a registered endpoint for days; the Stripe CLI's
forwarding does not. It was decided on 2026-10-09, below.

**Implementation note, 2026-10-09 (lost-event recovery).** A lost event is recovered by a catch-up
sweep and, behind it, an aging rule. Four alternatives were weighed: taking the status seen at charge
or lookup as a fact; re-checking each Processing payment on a timer; listing the account's recent
events; and sending a silent payment to review after some days. The last two were chosen together.
Listing events needs no change to how a fact is received, reuses the reader, the deduplication by
event id and the reference lock, and will cover disputes, refunds and payouts when they are read.
The first two would each read a payment's state, which is a second source for the same fact and
covers only payments.

- **The sweep runs from the payment worker, behind the processor seam.** At most every five minutes
  for each fixture, and only when a payment is waiting for an outcome. It lists the connected
  account's events since the oldest waiting payment was created, never further back than 29 days,
  one day inside the 30 Stripe keeps them. No position is stored and there is no migration. The
  simulator's sweep returns nothing.
- **A swept event is read as a delivered one is.** The same reader, so the same checks, and the same
  observation: an event seen both ways is stored once and is never a conflict. It carries no
  signature. It is trusted because the list was asked of Stripe with the platform's key for one
  named account, and an event naming another account is not taken.
- **Seven days without an outcome sends a payment to review**, as `outcome_overdue`, only directly
  after a sweep that succeeded. An administrator can close it. This rule is in the shared engine, so
  a simulated payment left seven days without a success is sent to review too.
- **An overdue payment does not return to Processing.** A late failure ends it as failed. A late
  success sets the paid date and leaves it with a person, so that nothing can post for a payment
  someone may already have corrected by hand.
- **A review reason survives a failed attempt.** Before this, a technical failure on the attempt that
  judged a late fact replaced the reason and returned the payment to Processing, from where bank
  evidence could post a receipt. That was true of every review reason, and is corrected for all.

It was run against a sandbox on 2026-10-09 with no event forwarding at all. A declined-card payment
was accepted, waited in Processing, and was ended as failed by the next sweep, which listed one
event. The host's first sweep listed fifteen events for payments of earlier runs, all of them already
held or stored once, and none became a conflict. Stripe accepted the filter on event types. Nothing
posted.

Two limits are accepted. A payment that has left the waiting state is no longer swept for, so a later
event reaches it only by delivery. And when disputes and payouts are read, the sweep will need a
window that does not depend on a payment waiting, because those arrive for payments that are not.

The provisional assumptions of ADR-053 stay open. This ADR verifies none of them, and nothing here
is approval to move real money.

## Revisit trigger

Revisit when the probe's findings contradict a decision above; before any payment form or real
mandate is added; before a payout posts without separate bank evidence; before the adapter sends
anything that identifies a tenant; and before any mode that can reach a live Stripe account exists.
