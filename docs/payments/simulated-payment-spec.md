# One-time simulated payment implementation specification

- **Audience:** Implementers and reviewers of issue #456
- **Status:** Implemented simulation contract for #456; live payments remain unapproved
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-09

## Evidence and boundary

[ADR-046](../adr/ADR-046-simulated-payment-recognition.md) owns the simulation recognition decision.
[Stripe research](../research/stripe-connect-payment-lifecycle.md) owns dated provider facts.
`PaymentLifecycleEvidenceTests` exercises the actual Accounting engine, projections and invariant
sweep. `SimulatedPaymentTests` covers durable state, transport, isolation and receipt atomicity.
The [simulation runbook](../runbooks/payment-simulation.md) owns fixture commands and recovery.

The conventions here apply only to a deterministic, direct-charge-shaped simulator with one
synthetic connected account bound to one fixture organization and one explicitly selected trust
bank. They do not approve that funds flow for live use. No card numbers, bank credentials, mandates,
Stripe secrets, provider network calls or production enrollment enter this slice.

## Request and immutable facts

The tenant portal submits `amount`, `currency: USD` and a client-generated UUID idempotency key.
Allow $0.01 through $10,000.00 inclusive, at most two decimal places; reject rounding, exponents
that deserialize out of range, other currencies and unknown writable selectors. This cap is a
simulation convention, not a provider limit. Positive payments may be partial or exceed current
receivables; Accounting's existing excess-to-prepayment rule applies. The displayed balance is
informational and is never a client-provided allocation instruction.

The host revalidates the active resident link on every submit/read and supplies org, tenant and
actor from the authenticated binding. Bank and synthetic provider account come from the trusted
fixture binding. The browser cannot choose org, tenant, owner, property, bank, posting date or
scenario. CSRF protection and rate limits apply to the POST. The caller can read only operations
for its currently linked tenant; a same-org stranger receives the same not-found response as an
unknown operation. Revoke access immediately when the link is revoked. Previously accepted work
continues under a durable system actor: session expiration must not discard financial obligations.

Persist an immutable operation UUID, org/tenant/initiating actor IDs, amount/currency, fixture
generation, provider-account/bank binding version, request fingerprint, idempotency key and creation
instant. Scope uniqueness to `(org, initiating user, key)`; fingerprint the canonical tenant,
amount in canonical two-decimal form, currency, payment method, confirmed fee and binding version. Same key/same fingerprint returns the same
operation, including after completion; same key/different fingerprint returns conflict before any
dispatch. Two different keys are two explicitly confirmed payments. A confirmed failed collection
may be retried by a new operation/key; automatic transport recovery always retains the old key.

Store append-only normalized observations with provider event/object IDs, account, mode, event
kind, observed/received instants, evidence references and content fingerprint. A repeated event ID
with changed content is quarantined, never overwrites the first observation. Retain journal IDs
and immutable evidence-to-effect links. A mutable derived summary and worker lease are caches of
those facts, not authority to rewrite them. UTC instants order receipt/audit only; the bank evidence
carries an explicit accounting `DateOnly`. Never derive its date from webhook delivery time.

## State and user wording

Maintain independent collection, payout and accounting facts; derive the public status below.
An event's timestamp is not a version number. Append a late observation, then reduce the complete
validated fact set. Contradictory facts require review, except the explicitly allowed late return.

| Derived status | Meaning and next allowed outcome                                                                                                                                                                             | Tenant wording                                                              | PM wording/action                                                       |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | --------------------------------------------------------------------------- | ----------------------------------------------------------------------- |
| Requested      | Intent/outbox committed; provider acceptance unknown. Dispatch to Processing or confirmed Failed.                                                                                                            | Simulation requested                                                        | Awaiting dispatch; inspect stalled retries                              |
| Processing     | Accepted or provider progress seen; no journal receipt. May become Settled, Failed, or Needs review.                                                                                                         | Simulated payment processing — not yet on your ledger                       | Awaiting bank evidence or retrying technical delivery                   |
| Failed         | Authenticated definitive collection failure before any bank credit. Terminal for this attempt; a new request can retry.                                                                                      | Simulated payment failed — no payment recorded                              | Collection failed; no ledger effect                                     |
| Settled        | Complete gross bank-credit evidence and receipt journal link committed together. A late return can lead to Needs review.                                                                                     | Simulated payment recorded                                                  | Bank evidence matched; receipt posted                                   |
| Needs review   | Conflicting/unsupported evidence, posting rejection, return requiring an accounting decision, or no outcome seven days after creation (`outcome_overdue`). Technical recoverable failures remain Processing. | Simulated payment needs review; show whether a receipt was already recorded | Stable reason code, evidence reference, last attempt and allowed action |
| Returned       | Return evidence plus a linked reversal committed by a PMAdmin through the guarded return (ADR-052). Never derived from a return notification alone.                                                          | Simulated payment returned by the bank — the receipt was reversed           | Receipt and linked reversal references                                  |
| Review closed  | A PMAdmin closed a review with a note and no posting (ADR-052). A later observation reopens it as Needs review.                                                                                              | Simulated payment review closed; show whether a receipt remains recorded    | The note, the reason and the evidence reference                         |

Failed is terminal for ordinary notifications, not permission to discard a contradictory bank
credit. Such a credit changes the exception summary to Needs review and produces no posting. Settled
is not irrevocable: a subsequent return is retained and shown. Duplicate or older Requested,
Processing, succeeded, availability or payout observations cannot regress Settled. A payout failure
does not mean the tenant's collection failed: remain Processing while awaiting replacement payout
evidence, or Needs review if it contradicts a recorded bank credit.

Provider success, balance availability, payout initiation and provider payout-paid all leave the
journal unchanged. Only a complete bank-credit evidence bundle can advance to Settled. The
simulator may deliver that bundle first: it contains its own payment/account/payout attribution and
all required amounts, so earlier progress events are not prerequisites. An incomplete bundle waits
for missing facts; it is never assumed complete because a timer expires.

## Accounting contract

Use the existing `RecordPayment` command through the host adapter. Resolve effective property and
owner at the evidence's accounting date using the existing Accounting-to-Directory port. Resolve
these when executing the receipt command, not from the tenant's present lease or request
time. Missing/ambiguous date-effective attribution is Needs review. No fallback to the oldest bank
or current owner. Freeze the resulting journal reference; later reversals retain original line
dimensions, even if ownership changes. Every row carries the ambient org.

For gross receipt `G`, let `R` be the existing engine's tenant-wide open receivable at posting,
`A = min(G, R)` and `E = G - A` (assuming the engine's valid nonnegative balance state).
Zero lines are omitted. The exact current template is:

| Account              | Direction/amount | Basis   | Dimensions in addition to org |
| -------------------- | ---------------- | ------- | ----------------------------- |
| Trust bank           | Debit G          | Both    | Bank                          |
| Tenant receivable    | Credit A         | Accrual | Tenant, property, owner       |
| Owner equity         | Credit A         | Cash    | Property, owner, bank         |
| Prepayment liability | Credit E         | Both    | Tenant, bank                  |

No unit dimension is added by this event. `Both` participates once in each basis. Accrual owner
equity was recognized by the original charge; receiving cash clears its receivable. Use payment
method `ach`, description `Simulated tenant payment`, and stable source reference
`sim-payment:{operation UUID in N format}:receipt`. The fixture's explicit bank must be an
org-owned trust bank. The binding is immutable once used; changing the configured bank cannot
retarget an in-flight operation. Capabilities may gate submission only and cannot change lines.

| Evidence or action                                                                                                                                     | Journal effect and date                                 | Outcome in #456                                     |
| ------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------- | --------------------------------------------------- |
| Requested, processing, success, available, payout pending/in-transit/paid                                                                              | None                                                    | Await bank evidence                                 |
| Confirmed failed collection, with no credit evidence                                                                                                   | None                                                    | Failed                                              |
| Bank credit whose gross is the charged amount, whose fee is the quoted fee, and whose net is the ledger amount; exact currency/account/operation match | Receipt above for the ledger amount, bank evidence date | Settled atomically with journal link                |
| Fee or gross/net mismatch; batch mixes payments, refunds or reserves                                                                                   | None; no receipt for either gross or net                | Needs review: unsupported settlement                |
| Pre-posting return contradicts credit evidence                                                                                                         | None                                                    | Needs review; do not post and immediately reverse   |
| Late full/partial return, refund or disputed payment                                                                                                   | None automatically; preserve original receipt           | Needs review; a PMAdmin posts or closes it (below)  |
| Locked receipt period or missing attribution                                                                                                           | None; transaction rolled back                           | Needs review; retain original evidence date         |
| Technical failure or serialization contention                                                                                                          | None if rolled back                                     | Retry same effect/date; re-evaluate committed facts |

### Convenience fee on a payment (ADR-053)

A payment records the ledger amount (`amount`), the payment method (`card` or `ach`), the convenience
fee the tenant confirmed (`quoted_fee`) and the date the processor reported success (`paid_at`). The
charged amount is the ledger amount plus the fee and is derived, not stored. An organization with no
fee rule charges nothing, and such a payment is the fee-free payment described above.

`GET /api/portal/tenant/payments/quote?amount=&method=` returns the fee and the charged amount under
the organization's rule. The tenant confirms that fee in the submit request. If the rule no longer gives
that fee, the request is refused with 409 `fee_quote_changed` and nothing is created; an accepted
request keeps its fee whatever the rule does afterwards.

Only a clean item settles from single-payment evidence: the processor kept exactly the quoted fee, so
the bank received the ledger amount. The receipt posts the ledger amount with the payment's method. A
fee difference in either direction is `unsupported_settlement` in single-payment evidence, and so is
the return of a fee-bearing payment whose evidence is not the fee-free shape.

### Payouts (ADR-053)

A processor pays out many payments in one bank deposit, keeps a fee from each and takes returns out of
a later deposit. That evidence is a payout, and the
[fee and settlement specification](fee-and-settlement-spec.md) owns it: the lines, the checks, the
worked figures, the review reasons and how a payout reconciles. In short:

- A payout arrives through `POST /callbacks/payments/simulation/payout` or the fixture command, is
  stored once, and posts completely or not at all.
- A fee difference is the property manager's. It moves held PM fees, never a tenant or an owner.
- A payout that contains a return waits for an administrator.
- Staff see payouts in the **Payouts** list on the Operations page; an administrator posts a waiting
  one or closes it with a note.

Single-payment evidence, as this document describes it, remains valid for the clean case. Wherever
this document says a fee, a net amount or a batch is unsupported, above this section or below it, it
speaks of that evidence, not of a payout.

### Posting a return (ADR-052)

A full return mirrors every receipt line, in the same basis and original dimensions: credit bank G,
debit receivable A (accrual), debit owner equity A (cash), debit prepayment E (both), linked to the
receipt. It uses the evidenced return date in an open period, not the receipt date or server today,
with source reference `sim-payment:{operation UUID in N format}:return` and one durable `Return`
effect per operation.

Nothing posts on return evidence alone. A PMAdmin posts it with `POST /api/payments/{id}/return`,
which calls Accounting's `ReturnTenantPayment`. A raw reversal call enforces none of the policies
below, so Payments never calls one. No automatic cascading reversals or period unlocking are allowed.

| Condition                                                                  | Outcome                                            |
| -------------------------------------------------------------------------- | -------------------------------------------------- |
| One complete full return, receipt untouched, return date in an open month  | Linked reversal posted; Returned                   |
| Reversal would leave the tenant's prepaid credit in that bank below zero   | 409 `return_prepayment_consumed`; nothing posted   |
| Reversal would leave the owner's cash equity below zero, per bank or total | 409 `return_owner_funds_disbursed`; nothing posted |
| Return date before the receipt date                                        | 409 `return_precedes_receipt`                      |
| Return date in a closed period or a reconciled bank month                  | 409 `return_period_locked`; no date substitution   |
| Return amount differs from the payment, carries a fee, or is incomplete    | 409 `return_partial_unsupported`                   |
| A refund or dispute, with or without a return                              | 409 `return_kind_unsupported`                      |
| Conflicting observations, or returns that disagree                         | 409 `return_conflicting_evidence`                  |

"Below zero" means the lowest end-of-day balance on or after the return date, not today's balance:
the reversal is backdated, so money that arrived later cannot cover it.

A refusal is recorded as the operation's reason and the payment stays in Needs review. A PMAdmin can
then close the review with `POST /api/payments/{id}/close-review` and a required note of at most 500
characters. Closing posts nothing and keeps every observation; the note is returned to staff only.
An observation that arrives after the closure returns the payment to Needs review. A review with
reason `technical_failure` cannot be closed; it is retried. No returned-payment fee is charged by
either action.

A refund, a dispute, or a return that disagrees with the posted one, arriving after a return was
posted, returns the payment to Needs review with reason `evidence_after_return`. It can be closed but
not posted again. The tenant view reports every `return_*` reason and `evidence_after_return` as
`return_requires_review`.

### Reviewer examples

Fresh fixtures have a $1,000 rent charge. Examples use independent runs, not new golden values.

| Case                                              | Expected result                                                                                             |
| ------------------------------------------------- | ----------------------------------------------------------------------------------------------------------- |
| $600 gross receipt                                | Bank +600, receivable 400; cash equity +600; accrual charge equity unchanged                                |
| $1,200 gross receipt                              | Bank +1,200, receivable 0, prepayment +200; receipt cash equity +1,000                                      |
| $1,000 gross, $30 withheld, bank evidence $970    | Reject settlement; gross receipt would overstate bank by $30, net receipt would leave tenant owing $30      |
| Confirmed failure                                 | No receipt; original $1,000 receivable remains                                                              |
| Full $1,200 return with excess untouched          | A PMAdmin posts it; the linked reversal restores the original balances                                      |
| Excess consumed before full return                | The guarded return refuses it (`return_prepayment_consumed`); a raw reversal would make prepayment negative |
| Receipt date in a locked period                   | No receipt or effect record committed; no date substitution                                                 |
| Duplicate source ref, identical or changed amount | Existing Accounting rejects duplicate; Payments supplies durable payload comparison and idempotent replay   |

The existing sweep checks a bank-book equation; it does not inspect external bank evidence.
Adding a clearing asset without changing the model would place entitlement on one side while
bank cash has not arrived on the other. Recognizing earlier requires a separate approved model
of processor custody, obligations, fees, availability and losses, with revised per-bank invariants.
Neither `BankFee` nor migration opening entries are a substitute for this missing policy.

## Module seams and durable storage

Payments owns org-scoped operations, observations/inbox, outbox, and effect records. All new
org-scoped tables use the RLS helper and FORCE RLS, composite org/entity foreign keys, decimal
amounts, append-only evidence/audit records and existing actor attribution. Unique constraints
cover request key scope, `(org, provider account, mode, provider event ID)`, provider object binding,
and `(org, operation, effect kind)` for the sole receipt effect. Journal source refs add defense
in depth; they are not the idempotency API. Any repeated identity with a changed amount, account,
currency, tenant or evidence fingerprint produces conflict/review, never blind success.

Payments declares these consumer ports in its own `Contracts`; host implementations invoke the
owning modules via `ISender` on the ambient transaction. They neither query another module's
tables nor create connections/transactions:

- `IPaymentLedger.RecordSettledReceiptAsync`: accepts the trusted tenant/bank/date/amount/source
  reference and returns the journal ID by calling `RecordPayment`. No raw line-writing API.
- `IPaymentEligibility.ReadAsync`: batch tenant/date/bank requests returning a map of eligibility
  facts via Directory queries; failures distinguish unavailable data from no eligibility.
  This gives early feedback; posting still performs authoritative date-effective validation.

Host authorization supplies the bound tenant and initiating actor. Payments never imports Identity
or ResidentAccess entity types. The return command lives in Accounting, guards its financial
preconditions under the posting lock, and is reached through `IPaymentLedger.ReturnSettledReceiptAsync`,
which answers a guard refusal as a result so the caller can record it on the same transaction.

One Payments-owned `IPaymentProcessor` hides provider mechanics. `SubmitAsync` and `LookupAsync` take
one immutable request: the stable operation identity and fingerprint, what is to be collected, which
is the charged amount, its currency and the payment method, and when the operation was created, so
that a collection can be found again after the processor has forgotten the request. It also carries
the processor's own reference once LeaseBook has stored one, so that a lookup can ask for that
payment by name. The request carries nothing that identifies the tenant. Results distinguish accepted, definitively failed and
unknown outcome.

A callback is handled in two steps, both outside the org transaction. `Authenticate(raw body,
signature)` proves the body came from the processor; it does no I/O, and nothing in the body is
trusted before it passes. Only that step can produce the notice the next one reads, which an
architecture test enforces. `ReadObservationAsync` and `ReadSettlementAsync` then turn an
authenticated notice into an observation of one payment or the evidence for one payout, fetching from
the processor whatever the notice does not carry. Each answers one of three ways: the content; not
this host's to act on, which is acknowledged with HTTP 204 and dropped; or unreadable, which is
refused with HTTP 400 so the sender does not count it as delivered. The content carries
account/mode/object identity and evidence, not journal lines. The generation and bank on it are the
adapter's to supply: the simulator's notices carry them, and an adapter whose notices do not takes
them from its own binding for the account, never from the notice.

The simulator implements this interface first. No processor DTO leaks into Accounting. A second
implementation, the Stripe sandbox adapter of
[ADR-054](../adr/ADR-054-stripe-sandbox-adapter.md), implements submit, lookup, authentication and
the reading of a payment's events. It reads no payout: every notice is, as payout evidence, not this
host's. It cannot promise permanent
provider dedupe, because Stripe may forget an idempotency key after a day. It therefore looks a
payment up before every submit, by listing the connected account's payments from the operation's
creation time and matching the operation id it stored, and it never submits an operation more than
23 hours old; that one goes to review. Once the operation holds Stripe's reference, lookup retrieves
that one payment instead of listing, and holds it to the same checks; a stored reference Stripe has
no payment for goes to review. It reports a payment as accepted only when Stripe's state for
it is `succeeded`, `processing` or `requires_payment_method`, each of which ends without anyone
acting. Any other state goes to review, as does a payment found twice or found with another
operation, amount, currency or generation. A failed connection, a rate limit, a fault at Stripe and a
key or permission Stripe refuses are technical failures and stay retryable.

The adapter reads seven event types and drops every other as not this host's, until the work that
reads payouts. Nothing it reads is ever complete, so none can be the bank evidence a receipt or a
return posts from.

- `payment_intent.processing`, `payment_intent.succeeded` and `payment_intent.payment_failed` are the
  `Processing`, `Succeeded` and `Failed` observations the simulator also produces. A snapshot event
  carries the payment it is about, so reading one asks Stripe nothing. The observation carries the
  charged amount, no fee, no bank evidence identifier and no payout. It can set a paid date or end a
  payment as failed.
- `charge.refunded` is a `Refund`. The charge in the event carries the payment's metadata, so this
  too asks nothing. The amount is the total refunded so far and the evidence identifier is the
  charge's. Stripe's other three refund events say the same without the metadata and are dropped. The
  adapter never issues a refund.
- `charge.dispute.created`, `charge.dispute.funds_withdrawn` and `charge.dispute.closed` are a
  `Return` when the disputed payment was an ACH debit and a `Dispute` otherwise. A dispute event
  carries nothing of LeaseBook's, so the adapter asks Stripe for the payment the dispute names and
  holds it to the test a payment event gets: this fixture's generation, an operation id, test mode.
  It then asks for that payment's charge, whose payment method type decides the kind; a charge that
  cannot confirm an ACH debit leaves it a `Dispute`. The evidence identifier is the dispute's and the
  bank date is the day the dispute was opened, not the day of the event, so the events of one dispute
  agree with each other and raise one review. Both requests carry an identifier in the path and
  nothing else. A dispute about a payment that is not this fixture's, or that Stripe has no payment
  for, is dropped. When Stripe cannot be asked, a delivery is answered with an error so that it is
  not counted as delivered, and the sweep reads the event later.

A `Return`, a `Dispute` and a `Refund` each send the payment to Needs review with reason
`return_requires_review`, and a PMAdmin can close the review. None can be posted: a return posts only
from complete evidence, and Stripe's word about a dispute is not that. Posting a return waits for the
work that reads payouts, where the payout carries the debit.

`RecoverObservationsAsync` answers what the processor still holds about one fixture's payments since
a given time, as the same observations a delivery would have produced. The Stripe adapter lists the
connected account's events from Stripe's Events API, filtered to the event types it reads, and reads
each with the reader a delivery goes through, so a swept event and its delivered twin are the same
observation. These events carry no signature. The trust is that the list was asked of Stripe with the
platform's key, over TLS, for one named account; an event that names any other account is not taken.
Anything in the list marked live refuses the whole sweep, as does a list that cannot be read to its
end, and a payment or charge asked for about a dispute that Stripe marks live. An event that cannot be read is counted and skipped. So is a dispute whose payment or charge
Stripe could not be asked for; the rest of the list is still read, and the sweep is not counted as
complete. Stripe keeps events for 30 days.

## Transaction and recovery protocol

1. **Submit transaction:** under request org RLS, validate resident/binding and insert operation
   plus dispatch outbox atomically. An org/user/key advisory transaction lock serializes competing
   submissions; the next caller reads and compares the committed operation. Return HTTP 202 with an own-tenant operation URL; same request replays
   that result. Commit before contacting the processor.
2. **Dispatch claim:** a worker uses a fresh org scope, atomically claims a due outbox row with a
   bounded lease and commits. It calls Submit/Lookup outside the DB transaction. Persist result
   and provider-object mapping in a subsequent org transaction, then finish the dispatch row.
   Crash before call retries the same identity. Crash after acceptance uses Lookup then, only if
   definitively absent, Submit with the same key. Unknown outcome never creates a second attempt.
3. **Callback receipt:** enforce payload size and signature verification, resolve trusted account
   binding, enter its org transaction, insert immutable inbox record, commit, then acknowledge.
   Duplicate identical delivery is acknowledged. DB unavailability produces retryable failure,
   never acknowledgment before durability. No posting occurs inside the anonymous ingress handler.
4. **Effect transaction:** a worker establishes org scope, locks the operation, re-reads all facts,
   validates the complete bank bundle and effect uniqueness, calls the Accounting port, saves
   receipt link/effect/derived state and processed-inbox marker, and commits all together. The
   adapter uses the same DbContext transaction; an Accounting `SaveChanges` is not an early commit.
   Crash before commit rolls back everything; crash after commit replays the saved effect result.
5. **Failure bookkeeping:** allow the entire failed posting transaction to roll back before a fresh
   scope records the attempt/error and schedules retry or review. Re-check effect existence so a
   concurrent committed receipt cannot be overwritten by an obsolete failure summary. Never catch
   an exception and mark an inbox processed inside a transaction containing partial journal writes.

Lock operation before entering Accounting's existing lock order; no Accounting path may call back
into Payments while holding posting locks. Concurrency tests must include two distinct payments
against the same tenant and different callback IDs for one receipt. Lease expiry permits another
worker to recover dispatch; effect uniqueness and operation locking remain necessary even with
leases. A worker restart discovers pending work in every configured fixture org, with one separate
RLS transaction per org; no request-time platform scope or all-org SQL bypass.

Callback receipt and the worker's persisting of a provider result also share an advisory transaction
lock keyed by the provider reference. An operation first takes its reference when the result is
persisted, and a callback for that reference can arrive at the same moment. Without the lock each
could miss the other: the receipt finds no operation to wake, the worker finds no fact to judge, and
the fact is stored and never acted on. Whichever takes the lock second reads after the first has
committed. The order is fixed: receipt locks the event, then the reference, then the operation;
persisting a result locks the reference, then the operation. This applies to every processor.

After acceptance the worker makes no further provider call about one idle payment; a stored
observation wakes it. An observation that is never delivered is recovered in two ways, both run by
the worker and neither able to post.

**Catch-up sweep.** A payment is _waiting_ when it is Processing, holds the processor's reference and
has no paid date. For each fixture, once every five minutes, the worker reads the waiting payments in
that organization's transaction and then calls `RecoverObservationsAsync` outside any transaction,
whether or not a payment is waiting: a return, a dispute or a refund arrives for a payment that is
not. It asks for everything since the last sweep that completed, less ten minutes, or since the
oldest waiting payment was created if that is earlier. The first sweep in a process asks for 29
days, and none asks further back than that. Each observation that comes back goes through callback
receipt (step 3), one organization transaction each, so one already stored is dropped by its event
id. The time of the last complete sweep is held in memory and nowhere else: a restarted host sweeps
at once, over the whole 29 days. A sweep is complete only when the list was read to its end, every
event in it that needed Stripe to be asked was answered, and everything found was stored. One that is
not complete is logged and its window is asked for again at the next interval; it never stops due
work.

**Aging.** A payment still waiting seven days after it was created goes to Needs review with reason
`outcome_overdue`. This happens only directly after a sweep of that fixture whose list was read to
its end and whose findings were stored, so a payment is never sent to a person while the observations
that could end it have not been asked for. A dispute that could not be asked about does not hold
aging back: a dispute follows a success, and a payment that is aged has none.
It applies to every processor. A payment that is leased, due, between technical retries or holds an
observation not yet judged is left alone. Marking takes the provider-reference lock and then the
operation lock, the order given above. A PMAdmin can close the review with a note. An observation
that arrives afterwards is still recorded: a failure ends the payment as Failed; anything else leaves
it in Needs review, reopening a closed one, with the paid date set if it was a success. It does not
return to Processing, so no receipt can post for a payment a person may already have corrected by
hand.

A payment that holds both a failure and a success goes to Needs review with reason
`conflicting_evidence`, whichever arrived first. Neither is believed over the other.

A review reason is kept through a failed attempt. When the attempt that judges a late observation
fails for a technical reason, a payment in Needs review or Review closed under any other reason keeps
its status and that reason, and the attempt is repeated on the retry schedule below. When those
attempts run out it rests in review with the observation not judged, until another one arrives.

The simulator's driver always delivers, so its sweep returns nothing; aging still applies to it.

For the simulator, schedule technical retries at 1, 5, 30, 120 and 600 seconds after successive
failures, then Needs review. Time comes from an injected clock so tests advance it deterministically.
PMAdmin may retry technical failures through an authorized, audited command retaining the same
operation, payload, evidence date and effect key. Re-evaluate safety conditions each time. Review
reasons for fees, returns, contradictory evidence and closed periods have no force-post control;
the PM can inspect evidence and route the case for policy work. The tenant cannot retry a still
uncertain payment with an automatic new key.

## Callback trust and organization routing

The fixture bootstrap publishes a trusted host configuration manifest mapping synthetic provider
account + simulation mode to fixture org UUID, generation and exact trust bank UUID. Validate it
at startup against the fixture's database marker and org-owned bank using explicit org scopes.
This read-only fixture registry is the routing authority; there is no new globally readable table
of customer payment data and no request `app.platform` escape. Designing live account enrollment
and rotation is a subsequent task.

Verify the raw body with a fixture-only signing key and bounded timestamp tolerance before using
any account or object field. Then resolve account/mode in that manifest, establish org RLS and
look up the durable provider-object/operation binding. Org/tenant fields in payload metadata never
select context. A forged signature or stale timestamp returns generic HTTP 400. A valid event for an
unknown account/mode/type is ignored with generic HTTP 204 and bounded, redacted diagnostic counting;
it cannot create a binding. For a known account but not-yet-mapped object, persist a parked inbox
record under that org and retry association after dispatch recovery. Only trusted Lookup evidence
can establish the missing object mapping. Unmapped observations older than ten minutes appear in
the staff review count and in the staff list at `GET /api/payments/unmatched`, newest first and at
most 100. The list is read-only and shows only the received time, kind, amount, provider reference and
age; it never returns the payload, signature, account, bank, bank evidence or payout identifier.
They remain durable and can associate after dispatch recovery, after which they leave the list. Do not adopt
caller metadata. Wrong destination, currency or amount is retained for review without posting.

Simulated verification uses the same raw-body/authenticated-observation seam but does not claim
Stripe signature compatibility.

A host maps only the callback routes of its own processor. The simulator's two routes exist in a
Simulation host, and in the build of the published API contract, and nowhere else. A `StripeSandbox`
host has one route, `POST /callbacks/payments/stripe`, which is not part of the published contract.
All three are anonymous and outside cookie-authenticated `/api`. The simulator's routes read at most
16,384 bytes and share the rate limit of the tenant and staff payment routes. The Stripe route reads
at most 64 KiB and has a rate limit of its own, 1200 requests a minute for each remote address: a
processor sends its events in bursts from one address, and the Stripe CLI does not send again a
delivery that was refused.

The Stripe route verifies Stripe's own scheme. The `Stripe-Signature` header carries
`t=<unix seconds>` and one or more `v1=<hex>` values. Each `v1` is an `HMAC-SHA256` of the timestamp, a
dot and the exact body bytes, keyed with the signing secret in `Payments:Stripe:WebhookSecret`. The
comparison is constant-time, and the timestamp must be within 300 seconds of the host's injected
clock, either way. This is checked before anything in the body is parsed and before any database or
organization work.

The Stripe adapter must use the provider SDK's verification, and does: the Stripe library judges
whether a signature matches, on the host's injected clock and the same tolerance. Before handing a
delivery to it the adapter bounds the body and the header and refuses a header that is not Stripe's
shape. A test checks a signature worked out independently of both. The adapter reads an event member
by member, not through the Stripe library's event types, which reject a shape or API version they do
not expect.

After verification the adapter applies these rules, in this order:

- An event whose own `livemode` is not exactly `false`, or that lacks an id, a type or a creation
  time, is refused with HTTP 400.
- An event that names no account, or an account no fixture on this host is bound to, is acknowledged
  with HTTP 204 and dropped. No organization scope is opened and no database command runs.
- An event of a type the adapter does not read is acknowledged and dropped the same way.
- A payment counts only when its metadata carries this fixture's generation and an operation id. A
  connected account can also hold payments of the probe or of an earlier fixture generation; their
  events are authentic, not this host's, and dropped.
- The observation's generation and bank come from the host's binding, never from the event.

An authentic event for this fixture's payment in a three-letter currency other than `usd`, with an
amount that can be stored, is retained as a `Conflict` observation with that currency and amount, and
the payment goes to Needs review with reason `conflicting_evidence`. Only a Stripe sandbox host
admits that kind from its adapter. The amount is Stripe's integer divided by one hundred, which is
not the true amount in a currency without two decimal places; it is evidence for a person and never
posts. An event with no usable amount, or a currency that is not three letters, is still refused
with HTTP 400: there is nothing truthful to retain. A payment whose amount at Stripe differs from the
operation's is caught elsewhere: every attempt the worker makes retrieves Stripe's own payment and
holds it to the operation's amount, currency and generation.

## Executable non-live barrier and fixture lifecycle

In #456, implement these checks in executable startup, submission, callback and worker code:

- Default mode Disabled. Simulation is allowed only in Development or the integration-test host.
  Production and other environments reject Simulation at startup; Disabled exposes no payment
  mutation routes or worker. Any configured Live mode or real provider credentials is a
  startup error in this release. There is no live implementation registered. The application's only
  network client that can ask a provider to charge is the Stripe sandbox adapter below, which exists
  only in a `StripeSandbox` host; the Stripe library is referenced from the host alone, and an
  architecture test fails the build on a Stripe type in any module or outside the adapter's folder.
  The [Stripe sandbox probe](../runbooks/stripe-sandbox-probe.md) is a standalone maintainer script, not
  part of the application; it accepts only a test-mode key and stops at the first live-mode object.
- A third mode, `StripeSandbox`, registers the Stripe sandbox adapter in place of the simulator; a
  host has one processor, never both. Its configuration is admitted only when
  every one of these holds, and each is otherwise a startup error: the environment is Development;
  `Payments:Stripe:SecretKey` is a Stripe test-mode key (`sk_test_` or `rk_test_`), so a live key is
  refused by its prefix; `Payments:Stripe:WebhookSecret` is a webhook signing secret, `whsec_`
  followed by printable characters with no whitespace; the fixture signing key is present; every
  fixture is bound to its own connected account (`acct_`). `Payments:Stripe` is refused in every other mode, Disabled included,
  and a top-level `Stripe` section is refused in every mode, this one included. In every mode a
  setting under `Payments` that the host does not recognise is a startup error at any depth, and the
  refusal names the setting's path, never its value. A simulator account in a sandbox host, or a
  connected account in a simulation host, is refused. The Stripe key and the signing secret live in
  local secrets: a fixture manifest that holds provider credentials is refused.
- A sandbox host makes three further checks before it serves or collects, and the foreground worker
  step makes them too. A fixture's optional `CardPaymentMethod` and `AchPaymentMethod` must
  have the shape of Stripe's documented test payment methods, `pm_card_…` and `pm_usBankAccount_…`,
  which a payer's saved method never has; both settings are refused in any other mode. Stripe is
  asked whose key the host holds, and a key whose own account is a fixture's connected account is
  refused: a connected account with its own dashboard has test keys of its own, which pass the prefix
  check. Each fixture's account is then read with the key and must be a connected account that pays
  its own Stripe fees. A key Stripe will not answer for is refused the same way, and no refusal
  repeats Stripe's own message, which can quote part of a key.
- The sandbox adapter refuses any object Stripe marks as live, and sends a charge only the amount in
  cents, `usd`, the fixture's test payment method, automatic payment methods with redirects ruled
  out, an offline mandate for ACH, and the operation id and fixture generation as metadata.
- A binding carries the mode of the host that holds it. Evidence is kept only when it names the same
  mode as the binding it is for, and the simulator authenticates its own notices only in a simulation
  host, so a simulator notice cannot be stored in a sandbox fixture.
- A dedicated payment-fixture bootstrap creates new UUID organizations, tenants, users and trust
  banks and writes an immutable simulation marker/generation. Do not reuse demo/load/scenario or
  ordinary customer organizations, and do not change their golden figures. Explicitly register
  those IDs in the host manifest; matching an org display name or user role is insufficient.
- Every submission, inbox association and effect transaction verifies both the configured UUID/
  generation binding and persisted fixture marker. A stale generation, ordinary org or bank outside
  that org fails closed. No public API can add fixture status or choose the scenario.
- Tests advance an injected clock; a CLI-only scenario driver emits signed deterministic events
  and bank evidence. It uses a separate fixture credential unavailable to portal JavaScript. Its
  persistent provider store maps operation keys to immutable synthetic objects; replay/restart
  never invents a new accepted payment. IDs include fixture generation to isolate reset runs.
- Reset means recreating the dedicated disposable fixture database, then bootstrapping a new
  generation and identities. Refuse reset outside Development/test and refuse a target database
  without the dedicated fixture marker. Never delete journal rows through the runtime role or
  reset a shared demo/customer database. Old callbacks fail generation validation. Ordinary app
  restart preserves provider state, operations, inbox/outbox, key and bindings and resumes work.

Bank evidence is a separate synthetic artifact containing generation, account, destination bank,
payment/payout references, evidence ID, USD gross/fee/net, bank date and completeness. Only the
fixture driver may issue it, independently of a provider payout-paid event. One gross payment per
bank artifact is the supported convention. Partial, mixed or netted artifacts enter review.

## Acceptance matrix for #456

| Vector                                                                    | Required assertion                                                                         |
| ------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------ |
| Submit -> processing -> provider success -> available -> payout-paid      | Durable progress; zero new journal entries at each stage                                   |
| Separate complete bank artifact, delivered first or last                  | Exactly one correctly attributed receipt; earlier events cannot regress status             |
| Confirmed failure; user retry with new key                                | First operation has no receipt; new operation is independent                               |
| Double click; same key changed amount; same key another user              | Replay; conflict; separately scoped key with normal tenant authorization                   |
| Concurrent identical callbacks; distinct events same effect               | One receipt/effect; both observations accounted for                                        |
| Crash before/after provider acceptance and before/after journal commit    | Resume from durable state; neither lost obligation nor duplicate receipt                   |
| Worker lease expiry, application restart, parked callback before mapping  | Same provider identity; safe association and eventual single effect                        |
| Invalid signature/timestamp, wrong account/mode/generation, unknown event | No cross-org reads or receipt; generic response and bounded diagnostics                    |
| Different tenant in same org; another org; revoked link                   | No submit/read access to someone else's payment; accepted jobs remain durable              |
| Partial/excess receipt; simultaneous payments                             | Real engine split, exact lines/bases/dates/dimensions and per-bank invariant sweep         |
| $1,000/$30/$970 net settlement, incomplete batch, changed evidence        | Review with zero automatic receipt, no use of existing bank-fee template                   |
| Return before settlement; late return; consumed excess; payout failure    | Review or pending as defined; original receipt retained; no unrestricted reversal          |
| Locked period; missing date-effective attribution; technical failure      | Atomic rollback; correct review/retry reason; date/key unchanged                           |
| Owner changes between request and bank date                               | Existing date-effective attribution; journal link immutable afterwards                     |
| Production/ordinary org/real credentials/reset on wrong database          | Startup or transaction refuses; zero provider calls and zero journal writes                |
| PM retry after another worker succeeds                                    | Existing effect returned, settled state preserved                                          |
| Reload during processing; keyboard submit; screen-reader status           | Accessible simulation label, operation reference and honest wording; no automatic resubmit |

Add RLS integration and schema-guard tests for every new org table; architecture guards for module
references and the unchanged platform-scope call site; meaningful worker failure injection; portal
e2e; and the accounting invariant/property/golden suites. Existing staff payment interaction budgets
and seed figures stay fixed. Emit attributed request, progress, posting, review and retry audits;
telemetry includes latency/retry/reason and stable operation correlation, not payment credentials,
raw payloads or sensitive bank identifiers. PM views expose only their org's sanitized failure facts.

## Decisions required before real money

Live recognition/custody and clearing, approved charge/account model, actual fee payer and coverage,
ACH authorization/return handling, negative-balance responsibility, refunds and partial returns,
locked-period corrections, bank evidence/reconciliation controls and provider retention obligations
remain unresolved. They require provider/accounting/legal evidence and separate acceptance.
Simulation conventions cannot settle them. Account provisioning, secrets and deployment verification
belong to the existing distribution process; no configuration flag bypasses a future approved release.
