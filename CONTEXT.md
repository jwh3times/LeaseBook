# LeaseBook

Property-management software for small residential property managers, built around correct trust
accounting. This glossary fixes the language the codebase, ADRs and tests all use for the same
concepts, so that a term never means two things in two places.

It is a glossary and nothing else — no design, no decisions. Architectural decisions live in
[`docs/adr/`](docs/adr/). Terms are added when they are actually settled, not in anticipation, so
this file grows one resolved ambiguity at a time.

## Language

### Access planes

Every piece of work in the system runs in exactly one access plane, and which one it is determines
what data is visible at all. The distinction is not a permission level layered on top of a shared
view — the two planes see disjoint things.

**Tenant plane**:
The access mode in which work sees exactly one organization's data and nothing else. Every ordinary
read and write happens here, including everything a customer can reach.
_Avoid_: org plane, tenant scope, org context (as a name for the plane itself)

**Platform plane**:
The access mode in which work sees the state used to operate the product across organizations, which
belongs to no single one of them. Reserved for running the business itself; nothing a customer does
reaches it.
_Avoid_: admin plane, superuser mode, god mode

**Unit of work**:
A single atomic piece of work that carries one access plane for its entire duration. The plane is
established when the unit of work begins and ceases to exist when it ends — it is never ambient, and
never outlives the work it was established for.
_Avoid_: scope, session, request (each of these is a thing that _has_ a unit of work, not the unit
of work itself)

### Accountability

**Actor**:
Who is accountable for a unit of work — either a named person, or the system acting for a stated
reason. There is no third case: work no person is accountable for must still name the process that
did it. "Unknown" is not an answer the language admits.
_Avoid_: user (a person is a person; an actor is the role a person **or** the system fills),
author, created-by

### Capabilities

Whether a behaviour is available is answered from two independent sources, and conflating them is the
failure the whole design exists to prevent: an operator turning something off during an incident must
never be indistinguishable from a customer never having been entitled to it.

**Capability**:
A named behaviour that can be switched on or off for a given organization. What exists is declared in
code; the database holds only its state.
_Avoid_: feature (too broad — a feature is what the product does; a capability is the switch),
toggle, permission (a permission is about a person, a capability is about an organization)

**Feature flag**:
The operations answer to "is this on for this deployment right now?" — temporary, applies to everyone
at once, and expected to end in deletion.
_Avoid_: kill switch (that is one use of a flag, not the thing itself), setting

**Entitlement**:
The commercial answer to "is this organization allowed to have this?" — durable, per-organization, and
the sort of fact a customer can be told. Recorded as an append-only history of grant and revoke
events, never as a mutable row.
_Avoid_: licence, subscription, plan, permission

**Cohort**:
Membership in a staged rollout, for an organization or one of its users. Widens availability; never
narrows it.
_Avoid_: beta group, segment, audience

### Bulk runs

**Confirm**:
The operator's decision to execute a previewed run over a selection of targets, and the one
transaction that carries it out. It names the whole act — not a step within it. Until 2026-08-09 the
word also named the method each run type used to do its own posting, so "confirm" meant two things one
call apart; it now means only the first.
_Avoid_: commit (that is the transaction's own word), submit, apply, execute

**Run plan**:
What a run intends to do for each selected target, decided before anything is posted: post this, or
record this target as skipped or excluded for this reason. It is per-run-type knowledge and nothing
else — it holds no loop, no posting and no persistence, and it is what a run type contributes beyond
its preview.
_Avoid_: batch (a plan is not sized or chunked), work list, queue, job
