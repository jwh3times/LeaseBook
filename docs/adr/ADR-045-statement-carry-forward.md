# ADR-045: A statement carries forward from the one issued before it

- **Status:** Accepted
- **Date:** 2026-09-13
- **Deciders:** Jerry Holland

## Context

ADR-040 made an issued owner statement an immutable artifact: the stored PDF is exactly what the
owner was sent. It did not make the statement's _figures_ durable. Every statement is built live from
the journal, and the artifact row stores owner, period, basis and an opaque key — nothing that could be
compared with a later read.

A posting can legally be dated into a month whose statement was already issued. Delivery checks only
the fiduciary tie-out, which compares two live reads of the same journal and therefore always agrees.
The reconciliation lock cannot close the gap either: it is per bank account and per month, it gates
only lines on a bank-class account, an owner statement spans every bank account touching the owner,
accrual `RentCharged` and `FeeCharged` lines carry no bank line at all, and an administrator can reopen
a lock with a reason. Charges, voids, bank adjustments, bulk runs, deposit application and ownership
transfers all accept a caller-supplied past date.

The visible failure is between consecutive statements. August is issued with an ending balance of
1,000.00; an August-dated −120.00 correction is posted afterwards; September is built live and opens at
880.00. The owner holds two documents that do not chain, and nothing in either explains why.

Three remedies were considered:

- **Lock the month before delivery.** The only lock whose coverage matches a statement is the
  organization-wide period close, which has no caller, no UI and no reopen. Making it a delivery
  precondition turns "send the owner a statement" into "close the books permanently first."
- **Issue an amended statement.** A new document type and flow — and September still would not chain
  to the August the owner already has unless it too referred back.
- **Detect it in the nightly sweep.** A backdated posting is legal activity, so flagging it as an
  invariant violation trains people to ignore the sweep; and an identity check between live reads is
  unfalsifiable by construction, the trap the ADR-016 addendum already recorded.

## Decision

**An issued statement is true as of issuance. Activity later dated into or before its period is
carried forward, itemized, on the owner's next statement.** Nothing is locked, amended or overwritten.

1. **An artifact records what it presented.** `statement_artifacts` gains nullable `property_id` (null
   for a whole-owner statement), `ending_balance NUMERIC(14,2)` and `as_of timestamptz` — the instant
   the statement's figures were read. They are written in the same insert that issues the artifact and,
   like every column on that table, never updated. A check constraint requires `ending_balance` and
   `as_of` to be both present or both absent. Artifacts issued before this decision keep nulls and are
   **not backfilled**: re-reading the journal now would record today's figure as the one the owner was
   given — the posture ADR-039 and ADR-040 §6 took with pre-existing rows.

2. **The anchor is the preceding month's artifact, exactly.** For a statement of period P, the anchor
   is the newest artifact for P−1 with the same owner, basis and property scope that recorded its
   figures. It counts from issuance regardless of whether any delivery attempt succeeded — a failed send
   is still a document the manager issued and can retry. No such artifact means no carry-forward, which
   is the behavior before this decision. A skipped month is never bridged, since that month's ordinary
   activity would otherwise be presented as "adjustments."

3. **The issued figure is authoritative.** Accounting computes, per anchored owner:
   `Total = Beginning − IssuedEnding`, which is exact by definition. The itemized lines are the entries
   dated before the period whose `posted_at` is after the anchor's `as_of`, plus any opening position
   dated into the period (Beginning includes it and the prior period's ending never can, whenever it was
   posted), each shown with both its booked date and its posted date. Because `posted_at` is stamped
   when an entry is posted but the row is visible only at commit, an entry whose transaction was open
   during the anchor's read can escape or double itemization — and a bulk run commits all its entries at
   its end, so that window is as long as the run. Whatever the lines do not explain is
   `Unitemized = Total − Σ lines`, rendered as its own labelled line and logged under
   `StatementCarryForwardUnitemized` (event id 1400). The difference is never silently absorbed.

4. **The section renders only when it says something.** It appears directly after the beginning balance
   when an anchor exists and the total or the unitemized remainder is non-zero: the issued beginning,
   the itemized adjustments, and an **adjusted beginning balance** equal to the live `Beginning`. The
   sections, ending balance and tie-out are unchanged, so `IssuedEnding + Total + sections = Ending`.

5. **Preview, PDF, CSV and the issued artifact use one builder.** The in-app statement, the downloads
   and delivery all go through `StatementAssembler`, which resolves the anchor from the artifact
   history and passes it to the Accounting port. There is one definition of what the owner is shown.

6. **No new sweep invariant.** The protection is structural, and the identity
   `IssuedEnding + Σ lines + Unitemized = Beginning` is proven in the Accounting suite rather than in a
   nightly check that could not fail.

## Consequences

An owner's statements now chain: each opens where the previous issued one closed and names every
entry that changed the history in between, with the date it was booked to and the date it was actually
posted. That pairing is the audit trail across the two documents, which is what NCREC 21 NCAC 58A
.0117(d) asks records to provide. Whether any rule additionally requires an _amended_ statement is an
open question for the external compliance review; if it does, an amendment flow is built on top of this
design rather than replacing it.

Issuing a statement now fixes a figure in the database as well as a PDF in the artifact store, so the
figure an owner was given is queryable without opening the document.

The first statement after this decision ships has no anchor — every existing artifact is unanchored by
rule 1 — so carry-forward begins one period after the first issuance under the new code. LeaseBook is not
publicly deployed, so this affects only seed and development data.

`StatementView` gains `propertyId`, `carryForward` and `asOf`, which regenerates the typed client. The
demo organization issues no statements, so its golden figures and the statement visual baselines are
unchanged; the scenario organization carries a late-posted May recharge so its June accrual statement
exercises an itemized adjustment on real rows.

Telling the person posting a backdated entry that it will surface on an issued owner's next statement is
not part of this decision; it is tracked separately.

## Revisit trigger

Reopen if the compliance review concludes that a changed issued period requires an amended or reissued
statement, or if `StatementCarryForwardUnitemized` fires for a statement whose anchor was not issued
while a posting transaction was open — either would mean the issued figure and the journal can disagree
for a reason this design does not account for.
