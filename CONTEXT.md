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
