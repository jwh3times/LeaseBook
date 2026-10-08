# One-time simulated payment implementation specification

- **Audience:** Implementers and reviewers of issue #456
- **Status:** Implemented simulation contract for #456; live payments remain unapproved
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-07

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

| Derived status | Meaning and next allowed outcome                                                                                                                    | Tenant wording                                                              | PM wording/action                                                       |
| -------------- | --------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------- | ----------------------------------------------------------------------- |
| Requested      | Intent/outbox committed; provider acceptance unknown. Dispatch to Processing or confirmed Failed.                                                   | Simulation requested                                                        | Awaiting dispatch; inspect stalled retries                              |
| Processing     | Accepted or provider progress seen; no journal receipt. May become Settled, Failed, or Needs review.                                                | Simulated payment processing — not yet on your ledger                       | Awaiting bank evidence or retrying technical delivery                   |
| Failed         | Authenticated definitive collection failure before any bank credit. Terminal for this attempt; a new request can retry.                             | Simulated payment failed — no payment recorded                              | Collection failed; no ledger effect                                     |
| Settled        | Complete gross bank-credit evidence and receipt journal link committed together. A late return can lead to Needs review.                            | Simulated payment recorded                                                  | Bank evidence matched; receipt posted                                   |
| Needs review   | Conflicting/unsupported evidence, posting rejection or return requiring an accounting decision. Technical recoverable failures remain Processing.   | Simulated payment needs review; show whether a receipt was already recorded | Stable reason code, evidence reference, last attempt and allowed action |
| Returned       | Return evidence plus a linked reversal committed by a PMAdmin through the guarded return (ADR-052). Never derived from a return notification alone. | Simulated payment returned by the bank — the receipt was reversed           | Receipt and linked reversal references                                  |
| Review closed  | A PMAdmin closed a review with a note and no posting (ADR-052). A later observation reopens it as Needs review.                                     | Simulated payment review closed; show whether a receipt remains recorded    | The note, the reason and the evidence reference                         |

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
that a collection can be found again after the processor has forgotten the request. The request
carries nothing that identifies the tenant. Results distinguish accepted, definitively failed and
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

The simulator implements this interface first. No processor DTO leaks into Accounting. A future
Stripe adapter must resolve uncertain results after provider key expiry; it cannot promise permanent
provider dedupe.

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
Stripe signature compatibility. The later Stripe adapter must use the provider SDK's verification
and account/mode semantics established by the research note. It requires separate sandbox work.

## Executable non-live barrier and fixture lifecycle

In #456, implement these checks in executable startup, submission, callback and worker code:

- Default mode Disabled. Simulation is allowed only in Development or the integration-test host.
  Production and other environments reject Simulation at startup; Disabled exposes no payment
  mutation routes or worker. Any configured Live mode or real provider credentials is a
  startup error in this release. There is no live implementation registered and the application has
  no network client capable of charging a provider. The
  [Stripe sandbox probe](../runbooks/stripe-sandbox-probe.md) is a standalone maintainer script, not
  part of the application; it accepts only a test-mode key and stops at the first live-mode object.
- A third mode, `StripeSandbox`, is recognised and guarded but cannot run yet: a host configured for
  it refuses to start until its processor adapter exists. Its configuration is admitted only when
  every one of these holds, and each is otherwise a startup error: the environment is Development;
  `Payments:Stripe:SecretKey` is a Stripe test-mode key (`sk_test_` or `rk_test_`), so a live key is
  refused by its prefix; the fixture signing key is present; every fixture is bound to its own
  connected account (`acct_`). `Payments:Stripe` is refused in every other mode, Disabled included,
  and a top-level `Stripe` section is refused in every mode, this one included. In every mode a
  setting under `Payments` that the host does not recognise is a startup error at any depth, and the
  refusal names the setting's path, never its value. A simulator account in a sandbox host, or a
  connected account in a simulation host, is refused. The Stripe key lives in local secrets: a
  fixture manifest that holds provider credentials is refused.
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
