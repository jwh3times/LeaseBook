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
2026-10-08, at API version `2026-09-30.endive`, and answered all but the first and half of the last.
A run on 2026-10-09 answered the first. None of its answers contradicts a decision above. The recorded
payloads are under `tests/fixtures/stripe/`.

- **Does a sandbox produce automatic payouts, on what timing, and does it report their
  reconciliation as complete?** Yes. One automatic payout existed by 2026-10-09: created at 00:42
  UTC that day, under three hours after the first charges, with that day as its arrival date, although
  the account's schedule says a two-day delay. It was `paid`, with `reconciliation_status` of
  `completed`. Stripe listed 14 balance lines for it, and their net came to the payout's amount
  exactly. The lines were of four types beside the payout's own: `charge` for a card payment,
  `payment` for an ACH payment, `payment_failure_refund` for an ACH debit that failed, and
  `adjustment` for a dispute. A failed ACH debit and a dispute each carry a fee of their own.
  Stripe sent four events about it, recorded on 2026-10-11: `payout.created` and
  `payout.reconciliation_completed` in the second it was made, both `in_transit` with the
  reconciliation already `completed`, then `payout.paid` and `payout.updated` five minutes later.
  Each carries the payout and nothing of LeaseBook's, so what a payout covers still has to be asked
  for. No payout has failed, so `payout.failed` is not recorded.
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

A second run on 2026-10-09 recorded what a card dispute and a refund look like, which the first run
had not made. The probe refunds only to record the events; the adapter never does.

- **A dispute event does not carry the payment's metadata.** It names the payment by Stripe's id and
  nothing of LeaseBook's, for card and for ACH. Whose payment a dispute is about cannot be read from
  the event; the payment has to be asked for.
- **A card dispute says it is one; an ACH dispute does not say.** A card dispute carries
  `payment_method_details` of type `card`. On the ACH dispute that member is null.
- **A card dispute arrives open.** `pm_card_createDispute` produced `fraudulent` with status
  `needs_response`, as `charge.dispute.created` and `charge.dispute.funds_withdrawn` in the same
  second as the payment succeeded. No `charge.dispute.closed` followed. The ACH dispute arrived
  already `lost`, with all three events.
- **Either dispute takes the amount back and a $15.00 fee with it**, as one `adjustment` line.
- **A refund is four events.** `refund.created` and `charge.refunded`, then `refund.updated` and
  `charge.refund.updated`. Only `charge.refunded` carries the payment's metadata, on the charge. Its
  `amount_refunded` is the total refunded so far, and `refunded` is true only for a refund in full.
- **Stripe answered one charge with HTTP 500.** The first `pm_card_createDispute` charge was refused
  with `api_error`; the same request made again succeeded. The adapter already treats a fault at
  Stripe as a technical failure and repeats it under the same idempotency key.

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

Two limits were accepted at that date, and the next note removes both. A payment that had left the
waiting state was no longer swept for, and the window depended on a payment waiting.

**Implementation note, 2026-10-09 (returns and review-only facts).** The adapter now reads disputes
and refunds. Each sends its payment to review and none can post.

- **A dispute is asked about.** The event carries nothing of LeaseBook's, so the adapter asks Stripe
  for the payment it names and holds that payment to this fixture's generation. This qualifies the
  webhooks note above: reading a payment event still needs no fetch; reading a dispute needs two. The
  second is for the payment's charge, whose payment method type makes the fact a returned ACH debit
  or a card dispute. A charge that cannot confirm an ACH debit leaves it a card dispute, so that a
  person still sees it. If Stripe then answers differently on a later reading of the same event, the
  second reading is kept as conflicting evidence.
- **The events of one dispute raise one review.** They carry the dispute's id and the day it was
  opened, not their own, so they agree with each other.
- **A refund is read from `charge.refunded` alone**, the one refund event that carries the payment's
  metadata, with the total refunded so far.
- **A return cannot be posted from Stripe's word.** No fact read here is complete, and a return posts
  only from complete evidence. An administrator can review and close one. Posting waits for payouts,
  where the payout carries the debit. A test shows the guarded return refusing one for a
  receipted payment and exactly the ledger amount, where everything else would let the reversal post,
  so that one flag is what holds it.
- **The sweep no longer waits for a waiting payment.** It runs every five minutes and asks from the
  last sweep that completed, less ten minutes, and 29 days on the first sweep after a start. A
  dispute Stripe could not be asked about is passed over, the rest is kept, and the same window is
  asked for again. No position is stored.
- **A payment reported both failed and succeeded goes to review**, in the shared engine.
- **An event for our payment in another currency is kept as conflicting evidence**, not refused.
- **A progress fact's amount is not compared in the engine.** The simulator's progress facts carry
  the ledger amount and Stripe's carry the charged amount, so one rule would be wrong for one of
  them. Every attempt already retrieves Stripe's own payment and holds it to the operation's amount.

It was run against a sandbox on 2026-10-09. A card payment made with `pm_card_createDispute` went to
review within seconds, from the two dispute events as delivered. With forwarding then stopped, an ACH
payment made with `pm_usBankAccount_dispute` succeeded and was disputed about four minutes later; the
next sweep stored its three events as one return and sent it to review. A refund made at Stripe for
an older fixture payment was found by the same sweep. An administrator closed the return's review, and
the attempt to post it was refused. The host's first sweep listed fifty events over 29 days, among
them the probe's own disputes and refunds, and kept none that was not this fixture's. Nothing posted.

Accepted, to revisit before the ADR is accepted: a host's first sweep reads 29 days of events inline
in the worker, with up to two requests for each disputed payment in them; a host whose clock is more
than fifteen minutes ahead of Stripe's can miss an event between sweeps until it restarts; and a card
dispute's closing event can arrive weeks later and reopens a closed review without saying what is new.
These are now listed under [Known limits](#known-limits).

**Implementation note, 2026-10-11 (payouts).** The adapter now reads payouts. A payout Stripe marks
paid is stored with Stripe's own list of its balance lines and held, with no journal effect. Bank
evidence from a fixture-only command releases it: an amount equal to Stripe's net posts every line
or none through the existing settlement path, and any other amount sends the payout to review. No
posting template, money path or capability was added. These points were settled in code, on the same
terms as the notes above:

- **The seam has a third read.** A payout with its bank evidence, which is what the simulator
  delivers, and a processor's own account of a payout are different things, so the second has its own
  read and its own type, which carries no bank amount, date or evidence. Stripe's adapter answers
  "not ours" to the first for every event; the simulator answers it to the second. The recovery
  result carries payouts beside observations, and the Stripe callback route offers an event to the
  payout reader when the payment reader passes it over.
- **Two events are read.** `payout.paid` and `payout.failed`. The other four payout events are
  acknowledged and dropped. Both are in the sweep's filter, which now names nine types.
- **A payout belongs to the fixture bound to its account.** It carries no generation, so every
  payout on a bound account is stored, older generations' and the probe's included. Whose payments it
  holds is found line by line.
- **Reading a paid payout asks for its lines.** Its balance transactions with their sources, every
  page, by the payout's id; and, for each dispute among them, the disputed charge, as a dispute event
  asks. A list that cannot be had whole is thrown: a delivery then answers 500, and a sweep passes
  the event over and asks for the same window again. Nothing is stored of half a payout.
- **The lines are read only once Stripe has reconciled the payout.** Stripe documents a
  `reconciliation_status` of `completed` as the point at which the lines can be listed in full. When
  the event does not say so, Stripe is asked for the payout as it is now, by its id, because an event
  never changes. If that does not say so either, the payout is not read yet, on the terms of a list
  that cannot be had. Reading does not start from `payout.reconciliation_completed`: in the recording
  that event came while the payout was still in transit, before it was paid.
- **How a line is stored.** The payout's own line is Stripe's net and not an item. A `charge` or
  `payment` line whose charge names a payment is a Payment. An `adjustment` for a dispute is a Return
  for the whole charge with nothing given back, and a Fee beside it under the line's reference with
  `.fee` added, only when the charge confirms an ACH debit and the line is in USD, takes money back
  and nets to its amount less its fee. Any other dispute line is a Dispute; `refund` and
  `payment_refund` are a Refund; `payment_failure_refund` is a FailedDebit; anything else is Unknown.
  Those four kinds are stored with Stripe's own signed figures and are kinds nothing posts. No line
  is skipped, and a line for a payment LeaseBook does not know keeps the reference Stripe gave it.
- **A manual payout is not asked about.** It is stored without lines under the type `manual`. An
  automatic payout keeps Stripe's method as its type, so an instant one is unsupported as before.
  A payout's unsupported type is now judged before whether it has any lines. This is in the shared
  check: a simulated payout with no lines and a type other than `standard` now reads
  `unsupported_settlement` where it read `settlement_incomplete`.
- **A held payout is a status, not a table.** `AwaitingBank`, with three columns on the payout: its
  mode, the processor's figure, and when the processor said it failed. The bank amount is zero and
  the bank date and evidence reference are empty until a bank has spoken. A check constraint refuses
  any payout marked posted that holds no evidence reference.
- **A payout is checked when it arrives, without the bank.** Its lines are tied to Stripe's figure
  and judged as the specification says. One that could not post goes to review at once under that
  reason; any other waits. Nothing posts for a payout no bank has spoken for, whoever asks: an
  administrator who tries is told `bank_evidence_missing`.
- **The check that runs by itself judges a payout once.** When it arrives, and when bank evidence
  releases it. A payout in review is checked again only when an administrator posts it. The paid
  event arriving again, by redelivery or in a sweep, changes nothing, however often it comes and
  whether or not the cause has gone. This is in the shared engine, so a redelivered simulated payout
  in review is no longer checked again either.
- **Bank evidence.** `payment-simulation bank <org-id> <stripe-payout-id> <bank-amount> <bank-date>`.
  The body is signed with the fixture key under a purpose of its own, so a simulator notice cannot be
  presented as bank evidence, and is verified only in a sandbox host and within 300 seconds. Evidence
  is stored for any payout the processor reported by itself, whatever its status. For one that was
  only waiting it is followed by the check and the posting. For one already in review it is kept and
  nothing is rechecked until an administrator posts. The same amount and date again change nothing;
  different ones are not stored and mark the payout `conflicting_evidence`, a later correct amount
  included. A bank date after today, in UTC, is refused by the command and by the engine.
- **A bank amount that is not Stripe's net is `bank_amount_mismatch`.** It is judged before anything
  the lines say. Such a payout cannot be posted and can only be closed.
- **A failed payout is `payout_failed`.** The time is stored once. It sends the payout to review
  from any state, a posted or closed one included, and such a payout can only be closed. What it
  posted stays posted and its payments stay as they were; the time it posted is kept. A failure that
  arrives before the paid event stores the payout without lines, and a paid event after a failure
  changes nothing.
- **Carried from the earlier steps, in the shared engine.** A payout's return line is taken only for
  a payment that is settled or whose review holds a return, and never when a failure, a refund, a
  dispute or a conflict has been stored for it. A payment line is refused while a failure, return, refund, dispute
  or conflict is stored for the payment, judged or not; the check now takes the lock a fact is stored
  under, for every payment the payout names. A failed attempt on a receipted payment gives up its
  lease and is retried on the usual schedule, then rests until the next fact. A payout is found by
  account, mode and reference. A dispute fact is still never complete: a return posts only inside a
  payout an administrator posts.

Two payloads this step needed have not been recorded and were built from recorded ones: a
`payout.failed` event, which is the recorded payout with status `failed`, and a refund line in a
payout, which is a recorded charge line under Stripe's type name.

It was run against a sandbox on 2026-10-11, on a copy of the fixture database. The host's first
sweep read the one payout the sandbox has made, asked Stripe for its lines, and stored it with a
processor amount of $1,470.60 and fifteen lines that sum to it: nine payments, two returned ACH
debits each with its $15.00 fee beside it, and two failed debits. It went to review as unsupported,
for the failed debits. Bank evidence dated after today was refused. Bank evidence of $1,470.60 was
recorded and left the payout in review; the same again changed nothing; $1,470.59 was refused as
conflicting evidence. No journal entry and no payment effect was written at any point. A payout that
posts has not been shown in a sandbox: the sandbox has made no payout holding only this fixture's
good payments. That check is still to do.

Accepted, to revisit before the ADR is accepted: each sweep reads again the lines of every payout
paid inside its overlap, and a host's first sweep those of every payout in 29 days; a receipted
payment whose late fact cannot be judged after five tries rests until the next fact arrives; and
the staff surface shows a held payout by its raw status with a bank amount of zero. The staff API
returns the processor's figure and the time of a failure, and no screen reads either yet. The next
note removes that last limit, and changes the 500 a delivery answers when a list cannot be had to a 503.
The first two are now listed under [Known limits](#known-limits).

**Implementation note, 2026-10-11 (surfaces).** The screens, the journal description and the logs
now say what a sandbox payment and a held payout are. No posting template, money path or capability
was added, and the lines and amounts of every posting are the same in both modes. The maintainer
decided these points on 2026-10-11:

- **The last step lands in two parts, and this ADR stays Proposed.** The surfaces come first and the
  documents second: the sandbox runbook, the specifications and architecture brought into line, the
  glossary, and the known limits recorded here. The ADR is not accepted until a payout has been shown
  to post against a real sandbox.
- **The API reports the mode, and the simulation's screens stay word for word.** The staff and tenant
  payment lists carry `mode`, `Simulation` or `StripeSandbox`, and null where payments are not
  enabled for the organization. A sandbox host says "Test payment" where a simulation host says
  "Simulated payment", under the badge "Test payment — no real money moves". An absent or unknown
  mode reads as the simulation.
- **The description of a posting is mode-specific, and is the only thing in a posting that is.** A
  sandbox receipt is described `Stripe sandbox tenant payment` and its return is noted as the bank
  returning a Stripe sandbox tenant payment, so its reversal reads
  `Void — Stripe sandbox tenant payment`. The words follow the mode stored on the bank evidence or
  the payout, never the host's mode and never a capability. The simulation's descriptions are
  unchanged.
- **A held payout is shown as held.** It reads "Waiting for the bank", with the processor's amount
  and a sentence saying no bank evidence has arrived. The staff API returns no bank amount and no
  bank date until bank evidence exists; the stored zero is never shown as an amount. A payout that
  posted and that Stripe later said failed reads "Needs review — posted, nothing reversed". The
  reasons `bank_amount_mismatch` and `payout_failed` and the four line kinds nothing posts have words
  of their own.
- **Operational items no person can see will be recorded as known limits, not fixed.** They are
  written down in the documents part, under [Known limits](#known-limits). A failed ACH debit still
  sends its whole payout to review.

Three things were found while building and settled in code:

- **"Post return" is offered only when the engine would accept the evidence.** The staff view and
  the command read one rule, so the two cannot come apart, and no second flag was added. It is not
  offered for a refund, a card dispute, a partial return or conflicting evidence, nor for a sandbox
  ACH dispute before its payout. The ledger may still refuse what the rule allows.
- **A delivery that cannot be read yet answers 503, for payouts as well as disputes.** When Stripe
  cannot be asked whose payment a dispute is about, or a paid payout or its lines cannot be read in
  full or are not yet reconciled, the delivery is refused with the problem code
  `payment_callback_deferred` and logged as event `4608` at Warning, so that Stripe sends it again.
  It is no longer answered as an unhandled error. A failure to store what was read still is one. Only
  a failure to ask Stripe is deferred: a connection or library failure, a timeout, or an answer the
  adapter itself refuses, whose own sentence is logged. Any other fault in reading is an unhandled
  error, as the independent review asked.
- **A tenant's payment in review says the property manager is checking it, in the sandbox only.** A
  tenant never sees a reason code or the staff text for one. The simulation's tenant screen is as it
  was.

The log messages of the payment events no longer name a mode, with their ids and names unchanged. In
a sandbox host a delivery dropped as not this host's is logged at Debug, because Stripe sends every
kind of event an account has.

Checked against a real sandbox on 2026-10-11, on a copy of the fixture database. The staff and tenant
lists reported the mode as `StripeSandbox`, and the tenant list carried nothing else new. The one
payout the sandbox has made was read by staff with the processor's amount, $1,470.60, no bank amount
and no bank date. Neither payment in review for a dispute was offered a return to post. The host's
log named no mode, and no journal entry was written. The check read the API, not the screens in a
browser, and the sandbox description of a journal entry has not been seen there, because no sandbox
payout has posted.

**Implementation note, 2026-10-13 (a stand-in address).** The barrier now admits one more setting,
`Payments:Stripe:ApiBase`: the address of a local stand-in for Stripe. The maintainer decided it on
2026-10-13 so that the sandbox screens can be driven through a browser in continuous integration
with no call to Stripe (#545). The stand-in and those scenarios are later work; this note covers the
setting alone. Continuous integration still never calls Stripe. These rules hold at startup, before
any client is built:

- **Only a `StripeSandbox` host may be given it.** Every other mode refuses it as an unknown setting,
  Disabled included, and a fixture manifest that holds it is refused like one that holds a key.
- **The address is loopback http and nothing else.** The scheme is `http`, the host is exactly
  `127.0.0.1`, `localhost` or `[::1]`, and the port is written out. A closing slash is allowed. A
  user, a path, a query, a fragment, a space or a line break is refused, and so are `https`, any
  other host, a name that only begins with `localhost`, `0.0.0.0` and another spelling of a loopback
  address. The address is parsed, and the text given must then be exactly what the parsed parts
  spell.
- **The address comes with one fixed key, and that key comes with the address.** The key is
  `sk_test_leasebook_stand_in`, a constant in the code. Stripe's test keys are the prefix followed by
  letters and digits, so Stripe issues none of this shape. With an address, any other key is refused.
  With no address, this key is refused. So a key of Stripe's is never sent to a stand-in, and the
  stand-in's key is never sent to Stripe. The adapter refuses the same two cases again when it builds
  its client.
- **With an address, every base of the Stripe library is the stand-in's.** The adapter calls only the
  API base. The library's bases for OAuth, files and meter events are set to the same address, so
  none is left aimed at Stripe. With no address all four are the library's own, as before.
- **A refusal names the setting by its path and never repeats what was given.**

Everything else asked of a sandbox host is still asked: Development, the webhook signing secret, the
fixture signing key, and each fixture bound to a connected account with test payment methods. The
check that the key is the platform's is then answered by the stand-in. The setting is not a way to
reach another Stripe environment or a proxy in front of Stripe: nothing but this machine can be
named. `localhost` is trusted to mean this machine, as the machine's own name resolution says. No
posting template, money path or capability was added, and `Live` stays refused.

**Implementation note, 2026-10-13 (the stand-in scenarios).** The sandbox screens are now driven
through a browser, in continuous integration, against the local stand-in for Stripe that the note
above admits (#545). It answers the host from the recorded payloads and sends it signed deliveries;
it is a stand-in, not a sandbox, and shows nothing about Stripe that the recordings do not. Eight
scenarios run through the tenant portal and the staff Operations page: a card payment and an ACH
debit each to a payout that posts on bank evidence equal to the net; a card dispute; a refund in full and one of part;
a payout that fails before and after it posts; a bank amount that differs from the net; and the one
recorded payout with its fifteen lines, served as recorded but for the account and the payout's id.
Each sends what this ADR says it should to a person, and nothing posts that this ADR says should
not. The receipts the posted payouts make carry the description `Stripe sandbox tenant payment` in
the journal; no scenario reads it on a screen. The last scenario asserts that a payout holding both a payment and that payment's return
posts, and is marked as an expected failure, so that the first known limit under Payouts has a test
that turns red when it is fixed. Built answers are listed in the
[runbook](../runbooks/payment-simulation.md#what-the-stand-in-answers): among them `payout.failed`,
a payout that holds only this fixture's payments, and the fee on each charge. What "Not yet shown"
says of a real sandbox stands: a payout that posts has still not been shown against one. No decision
changes, and this ADR stays Proposed.

**Implementation note, 2026-10-26 (a payout that can only be closed).** The staff list no longer
offers **Post payout** for an `unsupported_settlement` payout, which posting refused every time. A
payout holding a payment and that payment's return now goes to review for one reason whichever of its
lines is checked first: `unsupported_settlement`, or `conflicting_evidence` when the payment already
has a receipt. Before, it read `conflicting_evidence` or `settlement_incomplete` depending on the
lines' names, and the second offered a Post that was then refused. Every lookup of a processor
reference in the payment engine and the staff lists is now made within its account, as the schema
and the payout check already were. Nor is **Post payout** offered for `settlement_untied`: every
figure that check reads is fixed, and other evidence under the payout's id is kept out. A payout with
no lines now reads `unsupported_settlement`, and a line naming a payment made for another bank or
generation reads `conflicting_evidence`; both read `settlement_incomplete` before. That reason now
covers only what may still change, a payment not known here yet or a return whose payment has no
receipt yet, and keeps its Post action. No decision changes, and the known limit stands.

**Implementation note, 2026-10-31 (API version `2026-10-28.endive`).** The adapter moved to Stripe.net
53.1.0, which sends that version, together with a fresh probe run,
`tests/fixtures/stripe/2026-10-31T01-15-52-187Z/`. Against the three earlier runs every kind of answer
and event was still recorded, and nothing was lost or changed type. A dispute gained
`submission_method` and, once closed, `intended_submission_method`, and a card dispute, recorded in a
payout line for the first time, holds `case_type` and `network_reason_code`. The adapter reads none of them. A payout from
2026-10-09, read again at the new version, came to the same lines.

The run also recorded the first refund lines in a payout, three of them in the payout of 2026-10-16.
They appear because refunds made on 2026-10-09 had by then been paid out, not because of the version.
Each has type `refund` and its refund as the source, which names the payment, and the adapter reads
them as `Refund` items, a kind nothing posts.

One thing the earlier runs could not show: events fetched from
`/v1/events` named `2026-09-30.endive` while deliveries of the same run named `2026-10-28.endive`. The
adapter reads no event's version. No decision changes.

The suite still replays the three earlier runs, whose references are pinned by hand throughout it, and
the new run is read through the adapter by its own tests. `scripts/stripe-probe.test.mjs` fails if the
pinned version has no recording, and if an earlier run holds a field the newest one has lost.

The provisional assumptions of ADR-053 stay open. This ADR verifies none of them, and nothing here
is approval to move real money.

## Known limits

These hold in the code as it stands on 2026-10-11. Each is tolerable in a development fixture that
one maintainer runs and that can post nothing without a person's bank evidence. None is fixed here,
and each is to be settled before any adapter that can reach a live account. Where a dated note above
says "to revisit", this list is where the item now lives.

**The worker and its catch-up sweep**

- **The sweep runs inline in the one worker loop.** It follows the pass over due payments and
  nothing else runs beside it. It reads at most 100 pages of 100 events, and each request to Stripe
  may take 20 seconds, so a long sweep delays the next pass. A fixture has few events; a real account
  needs a schedule and a bound of its own.
- **A restarted host reads up to 29 days of events in its first sweep.** No position is stored. That
  sweep makes up to two more requests for each disputed payment and lists the lines of every payout
  paid in those 29 days.
- **Every later sweep reads again the lines of each payout paid inside its overlap.** A sweep asks
  from fifteen minutes before the last complete one, and a paid payout in that window has its lines
  listed again. The stored payout is not changed by it; the cost is the requests.
- **A host clock more than fifteen minutes ahead of Stripe's can miss an event between sweeps.** The
  sweep's start is the host's own time less ten minutes of overlap and five of allowance. An event
  missed this way is read only by the first sweep after a restart.
- **A fault in reading a listed event is counted, not logged.** The sweep counts it as "to be asked
  about again" whatever the fault was, and the log line for the sweep carries the count and no
  exception. A sweep with any such event is not complete, so the next one asks from the same start.
  An event that can never be read, such as a payout with more than 10,000 balance lines, holds that
  start in place until the event is more than 29 days old.
- **Within one fixture, a payment whose claim throws stops the payments due after it in that pass.**
  It is logged as a worker failure and the pass is made again a second later. The payments behind it
  wait for as long as it keeps throwing. Other fixtures are not held up.
- **A late fact that cannot be judged is left as it is.** When a payment that has a receipt, or is with
  a person, gets a new fact and every scheduled attempt to judge it fails, it stays as it was until
  another fact arrives. No staff control retries it.

**Payouts**

- **A failed ACH debit sends its whole payout to review.** Stripe lists one as
  `payment_failure_refund`, which is stored as a kind nothing posts. Whether its two lines should net
  to the failure fee, so that the rest of the payout can post, is an open question and not a
  decision.
- **A payout that holds both a payment and that payment's return cannot post.** A return posts only
  against a receipt an earlier payout made. Such a payout goes to review as `unsupported_settlement`,
  decided for the payout as a whole so that its reason does not follow the order of its lines, and is
  offered no Post action. Stripe's ACH dispute test method disputes a payment minutes after it succeeds,
  so in a sandbox the two usually reach the same payout, as they did in the one payout recorded.
- **Closing a payment's review for an ACH return, before the payout that carries the return arrives,
  stops that payout from posting.** The payout then contradicts a closed review and can only be
  closed. The staff text in the sandbox says to leave such a review open.
- **The engine takes the adapter's word that a returned payment was an ACH debit.** The adapter
  decides it from the charge Stripe holds. For a settled payment the engine accepts a return line
  without comparing it with the payment's own method. An administrator still has to post the payout.
- **A payout that cannot be stored is not shown to staff.** One whose id, type or figures do not fit
  what is stored is refused and logged, and appears nowhere on Operations.
- **A held payout whose first check throws shows no reason.** The failure is logged and the payout
  stays "Waiting for the bank". It is checked again when bank evidence arrives.
- **After a fixture is reset on the same connected account, Stripe's earlier payouts come back.** The
  new fixture's first sweep stores every payout of the last 29 days. Their lines name payments the
  new generation does not hold, so each goes to review. A payout's fingerprint also includes the
  generation, so one stored under a generation and reported again under another would be marked as
  conflicting; the fixture commands never keep a database across generations.
- **A fixture database made before payouts were read needs its migrations applied.** The fixture
  script applies migrations only when it creates a database. A host from `main` cannot sweep against
  an older one until `dotnet ef database update` has run against it as the migrator role.
- **An amount in another currency is Stripe's integer divided by one hundred.** That is wrong for a
  currency without two decimal places. Such an amount is kept as evidence for a person and never
  posts.

**Disputes**

- **A card dispute's closing event can arrive weeks later and reopens a closed review.** It is
  stored as one more fact of the same dispute, and nothing on the screen says what is new.

**Screens and wording**

- **"Original receipt remains recorded" can be false.** After a return has posted, a payment that
  goes back to review, or whose review is then closed, still reads that way although the receipt was
  reversed. This is so in both modes.
- **A sandbox host whose first read of the payment list fails uses the simulation's wording.** The
  mode arrives with the list, so until one read succeeds the error says "Couldn’t load simulated
  payments".
- **The tenant's payment list carries reason codes no tenant screen shows.** A return reason is
  reduced to `return_requires_review`; any other, such as `outcome_overdue`, is returned as stored.
- **The fixture's seeded names say "Simulation" in both modes.** The organization, owner, bank,
  tenants and the opening rent charge are seeded the same way for a sandbox fixture.

**Not yet shown**

- **A payout that posts has not been shown against a real sandbox.** The sandbox has made no payout
  that holds only this fixture's good payments. Posting is proven in tests and against replayed
  payloads. The sandbox description of a journal entry has therefore not been seen there either. This
  ADR is not accepted until it has.
- **One payload was built, not recorded.** A `payout.failed` event, because none has occurred. A
  refund line in a payout was built too until the run of 2026-10-31 recorded three; tests that vary
  one still start from a charge line.
- **The screens were checked against a real sandbox through the API, not in a browser.**
- **Not every outgoing header is pinned by a test.** A test pins the fields of a charge and the
  `Stripe-Account`, `Idempotency-Key` and `Stripe-Version` headers. The library's other headers, its
  user agent among them, are not asserted.
- **Most tests replay payloads from an earlier API version.** They are held to the shape of the
  recording at the pinned version by a test, but they do not carry the fields that version added. A
  real host has not been run against a sandbox at `2026-10-28.endive`; only the probe has.
- **Whether an event on the platform's own account is signed with the same secret is not shown.**
  None was delivered. The adapter drops an event that names no connected account.

## Revisit trigger

Revisit when the probe's findings contradict a decision above; before any payment form or real
mandate is added; before a payout posts without separate bank evidence; before the adapter sends
anything that identifies a tenant; and before any mode that can reach a live Stripe account exists.
