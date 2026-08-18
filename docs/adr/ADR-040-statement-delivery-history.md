# ADR-040: Statement delivery is an append-only history

- **Status:** Accepted
- **Date:** 2026-08-18
- **Deciders:** Jerry Holland

## Context

`StatementDeliveryRecord` documented itself as append-only — "corrections are new rows, never
updates" — and then exposed a mutable `State` with a documented `Queued -> Sent | Failed`
transition. Its migration said the quiet part out loud: it skipped `RevokeAppendOnly` because "the
M8 Queued→Sent/Failed state transition will UPDATE the row". One row was being asked to be both an
immutable record of a send and a mutable operational status, and the grants sided with mutable.

The vocabulary had the same problem. `DeliveryState.Sent` was documented as "email accepted by ACS",
which is a statement about the provider, not about the owner. Nothing in the name says so, and the UI
rendered it as success.

Put together, the model cannot describe what actually happens. The provider accepts a message and the
recipient's server bounces it minutes later:

- Leaving the row at `Sent` claims a delivery that never occurred.
- Updating it to `Failed` erases the acceptance, so nobody can tell a message the provider never took
  from one it took and lost.
- Writing a second row is ambiguous — a reader cannot tell a status change from a fresh retry, and
  the two mean opposite things about whether the owner has the document.

There is also nothing an artifact belongs to. The PDF bytes are immutable and keyed, but the key
lives on the same row as the recipient and the status, so "send this statement again" has no way to
say _this_ statement. A retry either re-renders (and may produce different figures than the send it
follows) or copies the key by hand.

This settles before Track B/B3 wires ACS, not after. A provider integration written against the
current model would encode the confusion in the code that consumes webhooks, and B3's own checklist
already specified "delivery states queued/sent/failed persisted on `StatementDeliveryRecord`".

## Decision

**Statement delivery is an append-only history of three separate things, and status is a projection
of it — never a stored column.**

1. **Three tables replace one.** `statement_artifacts` is the immutable rendered document for an
   owner, period and basis. `statement_delivery_attempts` is one request to send one artifact to one
   destination. `statement_delivery_events` records what became of an attempt. All three call
   `RevokeAppendOnly`: the runtime role holds no `UPDATE` or `DELETE` grant on any of them, so the
   append-only claim the old entity documentation made is now enforced rather than asserted.

2. **Current status is the kind of an attempt's latest event.** `DeliveryStatus.Of` is the only way to
   ask. No row anywhere carries a status column, so a later fact cannot erase an earlier one — an
   acceptance followed by a bounce is two rows and both survive.

3. **Provider acceptance is named `Accepted`, and `Sent` is retired entirely.** The kinds are
   `Queued`, `Accepted`, `Delivered`, `Bounced`, `Failed`. `Accepted` means the provider took the
   message; only `Delivered` means the recipient's server did. No name in the vocabulary can be read
   as success for the wrong one.

4. **A retry is a new attempt against the same artifact,** never a new event on the old one. The
   artifact is not re-rendered, so a retry cannot deliver different figures than the send it follows,
   and the tie-out gate does not run again — the gate governs _issuing_ a statement, and that artifact
   was already issued through it. This is what makes a retry distinguishable from a status change:
   they are different tables.

5. **Recorded order decides, not the reporter's clock.** Each event carries a 1-based `sequence`
   within its attempt, unique on `(org_id, attempt_id, sequence)`. `occurred_at` is kept as evidence —
   the provider's own timestamp — but does not order the history: provider webhooks arrive late and
   out of order, and ordering on that column would let a stale acceptance callback overwrite a bounce
   already recorded. The `id` is not the ordering either, because UUID v7 does not sort the same way
   in Postgres and in .NET's `Guid` comparer.

6. **Carried-over rows keep a null `basis`.** The migration moves every existing row forward as an
   artifact, an attempt, and its events, mapping the old `sent` to `accepted`. `statement_deliveries`
   never recorded which basis was rendered, so the backfill leaves it null rather than guessing —
   the same posture ADR-039 took with pre-existing actor rows.

7. **The write surface stays three methods on `IStatementDelivery`:** `DeliverAsync` (issue and open
   the first attempt), `RetryAsync` (open another attempt against an issued artifact), and
   `RecordEventAsync` (append one fact). There is no method that changes a row, and Track B plugs a
   provider in at `RecordEventAsync` without needing another.

## Consequences

The scenario org can now hold the histories a demo needs and the old seeder had to fabricate. It
previously inserted terminal `Sent` and `Failed` rows directly, with a comment that the local seam
could not reach them; it now walks O-S2 through accepted → bounced → retry → accepted → delivered and
O-S5 through a plain failure, entirely through the real seam. Every event kind exists in real rows.

Two facts became answerable that were not: whether a message the provider accepted ever reached the
owner, and whether an owner who received a statement received the same one that bounced. Both are
recordkeeping questions an examiner can ask about a fiduciary document.

`POST /api/statements/{ownerId}/deliver` returns `DeliveryAttemptResult(ArtifactId, AttemptId, Status)`
instead of `DeliveryResult(Id, State)`. The frontend does not read the body — it renders "Queued for
delivery" off the 200 — so the UI is unchanged, and the generated client does not move either: the
endpoint never declared a response type, so the contract records that 200 only as `unknown`. Typing it
would be an improvement, and a separate one.

Rolling back is deliberately lossy, and `Down` says so: `accepted` and `delivered` both fold into
`sent`, `bounced` folds into `failed`, and retries survive only as extra rows for the same owner and
period. That is not a defect in the reversal — it is the ambiguity the old vocabulary had, restored
faithfully.

Recording an event costs a read of the attempt's existing events to compute the next sequence. Event
recording is provider-callback-rate, not request-rate, and the unique index means a concurrent
recorder fails loudly on the index rather than quietly producing two "latest" events.

The retry path has no endpoint yet. `RetryAsync` is exercised by tests and by the scenario seeder; the
operator-facing surface belongs with Track B, where there is a provider failure worth retrying.

## Revisit trigger

Revisit when an attempt needs to record _which_ provider handled it — a second email provider, or a
postal-mail channel — since destination would stop being an email address and `to_email` would become
a typed destination on the attempt. Revisit sooner if a delivery history is ever needed across orgs
in one read, which the per-org RLS policies deliberately do not allow.
