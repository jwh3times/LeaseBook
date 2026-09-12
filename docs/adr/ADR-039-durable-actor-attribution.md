# ADR-039: Actor attribution is durable

- **Status:** Accepted
- **Date:** 2026-08-18
- **Deciders:** Jerry Holland
- **Amends:** [ADR-010](ADR-010-ledger-write-command-surface-and-actor-attribution.md) — its actor
  seam stands; what changes is that a system actor is now persisted rather than stamped as a null

## Context

The glossary defines an actor as the named person or named system process accountable for a unit of
work, and `CONTEXT.md` states that unknown accountability is not admitted. `Actor.System(reason)`
enforces that at the call site: a caller must name the process rather than pass a nullable id.

The persistence layer never agreed. `AppDbContext` stamped only `_actor?.UserId`, so every system
actor wrote a null `created_by` / `actor_user_id` — the same null an omitted actor would have
written. `Actor` said so itself: the reason "is not in the schema and the row alone is
indistinguishable from an unattributed write."

The consequence is an audit read that cannot answer its own question. Facing a journal entry with a
null actor, an auditor cannot say whether it came from the demo seeder, the migration CLI, the
nightly invariant sweep, another background job, or an accidental omission. That is a fiduciary
record claiming an accountability property it does not have, weeks before the compliance review that
reads exactly these documents.

The platform plane already made the opposite choice: `platform_audit_events.Actor` is a non-nullable
string naming the operator identity. The organization plane — where the money is — was the
inconsistent one.

## Decision

**Actor means durable attribution, not a runtime declaration.**

1. Every actor renders to a reference: `user:<guid>` or `system:<process>`. `journal_entries` and
   `audit_events` each gain `actor_kind` (`user` | `system`) and `actor_process`, alongside the
   pre-existing user column (`created_by` and `actor_user_id` respectively).

2. A system process name is a stable identifier, not a sentence — lower-case segments joined by
   `.`, `:`, `_` or `-`, at most 64 characters. `Actor.System` rejects anything else. The constraint
   exists because the value is now persisted: a free-text reason is serviceable at a call site and
   useless in a column, since two spellings of one process no longer aggregate.

3. **Missing attribution fails before the write.** `AppDbContext` refuses an org-scoped write with no
   declared actor, and `PostingService` refuses to post one — checked beside the organization-context
   check, since both come from the same `OrgScopedExecutor` call and neither is recoverable
   afterwards. `IActorContext.Actor` returning null now means "no unit of work is open", never "the
   system did it". **Extended 2026-09-12: a partial declaration fails too.** A call site does not fill
   the attribution columns on an `AuditEvent` it writes by hand — `SaveChanges` stamps all three from
   the ambient actor, and a row declaring a different actor is refused rather than reconciled.

4. A database check constraint pairs the columns on both tables: `user` names a user and no process,
   `system` names a process and no user.

5. The audit reads name the process. `ActorName` renders `System (<process>)` for an automated write,
   through one shared label so the per-entry trail and the compliance extract cannot describe the
   same row two different ways. The response shape is unchanged — this is a value, not a new field.

6. **Rows written before this ADR keep a null `actor_kind`,** which the constraint admits as its
   third arm. There is no backfill.

7. **The rule generalizes: an invariant collaborator may be absent only if its absence throws**
   (added 2026-08-21). Optionality is not the hazard — silent degradation is. A collaborator that can
   be null is a check that can quietly not run, and a check that did not run is indistinguishable
   from a check that passed. `AppDbContext` may still take its actor, org and data-protection
   collaborators optionally, because it is constructed outside DI by the migrator, by EF at design
   time and by test fixtures; it earns that by failing closed (`_actor?.Actor ?? throw`) rather than
   by degrading. Anything that cannot fail closed takes its collaborators as required parameters, so
   the wiring is a compile error. `OptionalCollaboratorTests` enforces both halves.

## Consequences

An audit read can distinguish processes, so "what did the nightly sweep touch" and "what did a human
do" are separable questions for the first time. Both readers surface it: the per-entry trail and the
compliance pack's audit extract render an automated write as `System (invariant-sweep)` rather than
the bare `System` that collapsed every seeder, job and CLI verb into one actor. Persisting the fact
without surfacing it would have left the claim true of the database and false of the document an
examiner reads.

`IActorContext.UserId` survives as a convenience for domain fields that genuinely record a human —
who signed off a verification, who finalized a reconciliation — where null is a meaningful answer
rather than lost information.

The refusal is a behavior change on any path that writes org-scoped rows outside `OrgScopedExecutor`.
No such path exists in `src/` today; one written later now fails loudly instead of writing an
unattributable row.

Not backfilling is the deliberate half of the decision. For a row already in the table the process
that wrote it is precisely the fact that was never recorded, so any backfilled value would be
invented. A visibly incomplete audit trail is worth more than a confidently wrong one, and the null
arm makes the boundary between the two eras readable rather than hidden. Nullable columns are the
price: the invariant is enforced at the write path and by the pairing constraint, not by `NOT NULL`.

`principal-without-user-id` — an authenticated principal whose id claim will not parse — remains a
system actor. It is now visible as one in the data, which is the point: it is a real condition worth
being able to count, not an unattributed write.

Making the write fail immediately found 97 tests that had been writing unattributed rows. Every
container-backed harness built its executor over a freshly constructed `ActorContext` while handing
the `AppDbContext` none, so the unit of work wrote an actor nothing read — and the accounting harness
constructed `PostingService` without one at all. Both seams took the actor as optional, which is why
neither was noticed. Both parameters are now required, so the wiring is a compile error rather than a
silent null. The finding is itself the argument for the ADR: an attribution seam that tolerates
absence will be left absent.

**Two more instances surfaced afterwards (2026-08-21), which is what prompted decision 7.**
`PostingService` still took `IReconciliationLock` optionally, and its absence skipped the
reconciliation period-lock check in full — a skipped lock and an open period are the same observable.
`FinalizeReconciliationHandler` still took `IActorContext` optionally and wrote `actor?.UserId`,
which conflated two different nulls: a system actor legitimately having no human to record, and no
actor being declared at all. The accounting suite finalized reconciliations — the act that locks a
period — through a handler built with no actor context, so the second null was never exercised and
no test asserted `FinalizedBy` at all. Neither was a production defect: DI supplied both
collaborators everywhere. Both are now required, the acting user is asserted, and the pattern is
guarded rather than left to the next reader to notice.

**A third instance, in the rows themselves (2026-09-12).** Decision 3 made a _missing_ actor fail
before the write; it said nothing about a _partial_ one. `AppDbContext` built its own audit rows from
the `Actor` correctly, but the ten places that write an `audit_events` row by hand set the three
columns by hand too, and seven of them set fewer than three — the four seeders set none, so the row
recording that an organization was provisioned recorded nothing about what provisioned it. Those rows
landed in the null-`actor_kind` arm that decision 6 reserved for rows predating this ADR, claiming an
era they were not written in, and the administrator review surface's `System (automated)` filter —
which keys on `actor_kind = 'system'` — could not see them. Nothing rendered wrong, which is why it
survived: the three user-attributed sites still resolved a name from `actor_user_id`.

The columns are no longer a call site's to fill. `SaveChanges` stamps all three from the ambient actor
on any hand-added `AuditEvent`, in the same pass and for the same reason it stamps `org_id`, and
refuses a row whose declared actor disagrees with the unit of work's. This is decision 7's argument
one level down: an attribution seam that tolerates absence will be left absent, and three columns that
must be written together are three chances to write two of them. The guard is the write path rather
than a test enumerating the call sites, so a writer added later is correct without being enumerated.

## Revisit trigger

Revisit when a system process needs more than a name — a job run id, a correlation id, or the
specific CLI invocation — so that two runs of the same process can be told apart. That is a
foreign key to a run record rather than a wider string, and it should not be bolted onto
`actor_process`. Revisit sooner if the compliance review asks for retroactive attribution, which
would be a records-retention conversation rather than a schema change.
