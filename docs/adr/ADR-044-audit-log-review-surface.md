# ADR-044: Audit-log review is a separate read, and payloads are withheld by origin

- **Status:** Accepted
- **Date:** 2026-09-11
- **Deciders:** Maintainers

## Context

`audit_events` has held a complete, append-only record of every write to an organization-scoped row
since M0, and ADR-039 made its attribution durable, so an automated write names the process that made
it. Two surfaces read it, and neither answers a manager's question about what happened. The per-entry
trail (P56) shows one journal entry and its reversal. The compliance pack's extract keeps only the
money-touching entity types, over a period that must be fully reconciled, because it exists to be
handed to an examiner.

"Who changed this tenant's contact email", "who deactivated that bank account", "what did the nightly
sweep touch last night" are answered today by reading the database directly. That is the gap #321
names, and it also asks a question it does not answer: whether review is a page or a parameter on the
existing export.

Two facts about the data decide the rest. The auditing pass snapshots **every** column of the changed
entity into `before`/`after`, so what a reviewer can read is fixed by which entities are org-scoped —
a decision made column by column, far from any review surface. And Identity is deliberately not
org-scoped (pitfall E6), so no password hash, security stamp or recovery code has ever been written
to `audit_events`; the payloads that do exist are LeaseBook's own schema, with one exception.

## Decision

**Review is its own read, at `/api/audit`, and its own page.** It spans the whole audited universe
rather than the money-touching subset, and it accepts any period rather than a closed one. Filters are
period, actor, record type and event kind, applied server-side over a paged, newest-first list; the
list carries metadata only. One event's payload is a separate read behind it, so browsing a trail never
ships the snapshots underneath it. Both are `RequirePMAdmin`, for the same reason the compliance pack
is: this reads what everyone in the organization did.

The export is the same filtered rows as CSV, metadata only, capped — and a truncated export says so in
its own first row rather than looking complete. **Exporting is itself an audited event.** Taking a copy
of the organization's audit history is at least as audit-worthy as generating a compliance pack, which
has recorded its own generation since WP-8, and "who pulled the audit log" is precisely the kind of
question this surface exists to answer. The row names the exporting user, the filter narrowing and how
many rows were taken — never the rows themselves, which would put a second copy of the trail inside the
trail.

**Payloads are withheld by the origin of their content, not by guessing at sensitivity.** The one
audited column whose content LeaseBook does not author is `ImportRow.RawJson` — a row of the customer's
previous system's export, verbatim, with whatever columns that system chose to include — together with
the two fields derived from it. Those are replaced by a marker. A field is shown as withheld rather than
blank, because "you may not read this" and "this was cleared" are different facts about the record. A
payload that is not a flat object is withheld whole rather than rendered raw. A name-fragment denylist
masks a secret-shaped column from the day it lands.

**The filter vocabularies come from the model and from source, never from `SELECT DISTINCT`.** Entity
types are the table names of the org-scoped entities plus an enumerated list of the hand-written
`entity_type`s; actions are enumerated the same way. A vocabulary read off the data describes the rows
that happen to exist rather than the events that can occur, shrinks on a quiet organization, and costs
a scan of the largest table in the database to say so.

**Three guards carry what the code cannot state.** A source scan fails the build when a hand-written
`entity_type` or `action` literal is not in its catalog, so a new synthetic event cannot become
invisible to the filter. A model scan fails the build when an audited entity gains a free-form `*Json`
column that nobody has classified as LeaseBook-authored or externally-sourced, and when one gains a
secret-shaped column or becomes an Identity type. And the review order's `id` tiebreak is asserted
against the generated SQL, because it has no observable behaviour: Postgres breaks these ties the same
way with or without it, so a behavioural test passes on the regression.

## Consequences

A manager can answer an attribution question in the product. The compliance pack is unchanged and stays
the examiner's document; the two reads can disagree about scope without either becoming wrong. The cost
is a second read path over the same table, a supporting `(org_id, actor_user_id, occurred_at)` index,
and a redaction policy that must be revisited whenever an audited entity gains a column — which is why
the revisit is a failing build rather than a convention.

The vocabularies are now a thing to maintain: a hand-written audit row with a new `entity_type` needs a
line in the catalog. The build says so, and the alternative was a filter that silently stops offering
events that exist.

Nothing here changes what is recorded about a change: the auditing pass, its payloads and its
attribution are untouched, and the surface writes nothing on any read path but the export, which
records itself.

## Revisit trigger

Any of:

- An audited entity gains a column whose content originates outside LeaseBook — the redaction policy
  stops being "one import table" and needs a rule rather than a list.
- A reviewer needs to filter by a system process rather than by "the system" as a whole, which the
  single `actor` parameter deliberately does not express.
- The audit page becomes noticeably slow to open. It is **not** one of the four money-critical paths
  `perf-probe` measures (`docs/perf.md`), so nothing watches it automatically. The part that grows is
  the unfiltered `count(*)` the pager needs: on the `load` fixture's 31,513 audit rows it is an
  index-only scan at ~6 ms, and the newest-50 page itself ~0.1 ms. A wait there means the trail has
  outgrown offset paging, and the answer is a keyset cursor rather than a bigger index.
