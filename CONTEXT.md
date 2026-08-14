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
the authority and organization boundary the work carries. The planes are mutually exclusive ways
of acting, not disjoint sets of readable facts: explicitly global, read-only operating state can be
visible from either plane.

**Tenant plane**:
The access mode in which work can act on exactly one organization's data. Every ordinary read and
write happens here, including everything a customer can reach; globally readable operating facts do
not widen that organization boundary.
_Avoid_: org plane, tenant scope, org context (as a name for the plane itself)

**Platform plane**:
The access mode in which work can act on state used to operate the product across organizations.
Reserved for running the business itself; it does not grant access to ordinary customer data, and
nothing a customer does reaches it.
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

### Receivables and delinquency

**Tenant financial account**:
The tenant-level receivable and liability relationship attributed through the tenant's one active
lease to one unit, property and owner. A tenant may have pending and historical leases, but only the
active lease supplies current financial attribution.
_Avoid_: renter balance (a balance is one figure within the account), occupancy account

**Financial attribution**:
The owner, property and unit identity carried by a financial event. Posted attribution is a
historical fact: later lease or directory changes do not rewrite it.
_Avoid_: current lease lookup (that is how a new event obtains attribution, not the attribution)

**Property ownership transfer**:
The effective-dated handoff of a property from one owner to another. It determines the owner for new
financial events on and after that date and is a recorded transition, not an edit to past attribution.
_Avoid_: owner edit, property reassignment

**Deposit responsibility**:
The owner-attributed obligation for a security deposit held for a property. A property ownership
transfer hands this responsibility to the succeeding owner without moving cash or owner equity.
_Avoid_: deposit ownership (the deposit remains the tenant's liability until disposition)

**Open charge**:
The unpaid portion of a charge owed by a tenant. Allocating a payment or general credit reduces its
remaining amount; a linked reversal may cancel the charge or cancel an earlier reduction. A charge
stops being open when its remaining amount reaches zero.
_Avoid_: outstanding entry (the entry remains part of the record after the charge is paid), balance

**Rent obligation**:
The specific rent charge for one lease and rental period. A late-fee assessment names this charge
directly; tenant, lease and calendar month are not substitutes for the obligation's identity.
_Avoid_: rent balance, monthly tenant charge

**Rent due date**:
The calendar date on which a rent obligation is contractually due, derived from the effective lease
policy for that rental period. It is not the date on which the journal entry happened to be posted.
_Avoid_: charge date, posting date

**Late day**:
An elapsed calendar day after the rent due date. The calendar day immediately after the due date is
late day one.
_Avoid_: days since posting, grace day

**Late-fee eligibility date**:
The first date on which a late fee may be assessed for a rent obligation: late day five, or a later
day when the lease grants a longer contractual threshold.
_Avoid_: grace-period end, month end

**Assessment date**:
The date on which eligibility is evaluated and a resulting late fee is posted. An assessment is a
present-tense act; it is never dated in the future.
_Avoid_: as-of date (too broad), run period, posting month

**Payment allocation**:
The assignment of a tenant payment or general credit to open charges. Allocation satisfies the open
charge with the oldest due date first; when due dates tie, it satisfies the earlier charge first.
_Avoid_: payment aging (payments do not acquire an age), bucket netting

**Delinquent balance**:
The sum of the remaining amounts of open charges whose due dates have passed, measured as of a stated
date. It is a gross receivable figure; an unapplied credit is reported separately rather than used to
make an aging bucket negative.
_Avoid_: tenant balance (which may also include unapplied credit or prepayment), arrears total

**Aging bucket**:
A range of elapsed calendar days since an open charge's due date. Only the charge's remaining amount
belongs in a bucket; payments and unapplied credits do not have their own aging buckets.
_Avoid_: entry-date bucket

**Unapplied credit**:
A credit-side tenant amount that is not allocated to an open charge. It is shown separately from
delinquency aging; its kind determines whether and how it can be applied later.
_Avoid_: negative delinquency, negative aging

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
