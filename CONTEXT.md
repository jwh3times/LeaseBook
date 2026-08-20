# LeaseBook

Property-management software for small residential property managers, built around correct trust
accounting. This glossary fixes the language the codebase, ADRs and tests all use for the same
concepts, so that a term never means two things in two places.

It is a glossary and nothing else — no design, no decisions. Architectural decisions live in
[`docs/adr/`](docs/adr/). Terms are added when they are actually settled, not in anticipation, so
this file grows one resolved ambiguity at a time.

## Language

### Organizations and tenants

**Organization**:
The property-management company whose data and configuration form one isolation boundary.
_Avoid_: tenant, account, company, customer (when naming the isolation boundary)

**Tenant**:
A person or named party responsible under a residential lease.
_Avoid_: renter, resident, leaseholder

**Lease lifecycle status**:
The operational state of a lease as pending, active, or ended. It does not by itself determine whether
the lease applies to a date.
_Avoid_: lease effectiveness, current lease

**Lease effective on a date**:
A non-pending lease whose inclusive term contains the stated date. An ended lease remains effective
historically within its term, while a future or expired active lease is not effective on that date.
_Avoid_: active lease, current lease

**Lease effective during a period**:
A non-pending lease whose inclusive term overlaps the inclusive period.
_Avoid_: active lease, lease active in the period

**Tenant lifecycle status**:
The operational relationship with a tenant party as current, evicting, or past. It is independent of
the party's leases and financial standing.
_Avoid_: tenant status, payment status, lease status

**Tenant financial standing**:
A ledger-derived view as of a stated date containing delinquent balance and unapplied credit as
independent amounts. Both may be positive at once; financial standing is never manually maintained.
_Avoid_: tenant status, late status, prepaid status

**Unit occupancy**:
Whether a unit is occupied or vacant on a stated date, determined only by whether a lease is effective
for that unit on that date.
_Avoid_: unit status, manually occupied, manually vacant

**Unit availability**:
Whether a unit is operationally available or unavailable, independent of occupancy. A leased unit may
be unavailable, and an available unit may be vacant.
_Avoid_: unit status, occupancy

### Access planes

Every piece of work in the system runs in exactly one access plane, and which one it is determines
the authority and organization boundary the work carries. The planes are mutually exclusive ways
of acting, not disjoint sets of readable facts: explicitly global, read-only operating state can be
visible from either plane.

**Organization plane**:
The access mode in which work can act on exactly one organization's data. Every ordinary read and
write happens here, including everything a customer can reach; globally readable operating facts do
not widen that organization boundary.
_Avoid_: tenant plane, org plane, tenant scope, tenant context

**Organization context**:
The identity of the one organization carried by organization-plane work.
_Avoid_: tenant context, org context

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
Who is accountable for a unit of work — either a named person, or the system acting as a named
process. There is no third case: work no person is accountable for must still name the process that
did it. "Unknown" is not an answer the language admits — and since ADR-039 not one the data admits
either, because the process name is persisted rather than living only at the call site. A process
name is a stable identifier (`seed:demo`, `invariant-sweep`), not a description of the occasion.
_Avoid_: user (a person is a person; an actor is the role a person **or** the system fills),
author, created-by

### Bank accounts

**Bank account**:
A real-world financial account held by the property manager. This is the generic category for both
fiduciary trust accounts and the property manager's own non-trust account.
_Avoid_: trust bank account (as the umbrella term), trust and operating account

**Bank purpose**:
The immutable classification of a bank account as operating trust, security-deposit trust, or PM
operating. It fixes whether the account holds fiduciary funds and belongs inside the trust equation.
_Avoid_: account type, bank type

**Operating trust account**:
A fiduciary bank account used for rent receipts, owner funds, and owner disbursements. It is inside
the trust equation.
_Avoid_: trust account, operating account

**Security-deposit trust account**:
A fiduciary bank account used to hold security deposits. It is inside the trust equation.
_Avoid_: deposit account, deposit bank

**PM operating account**:
The property management company's own non-trust bank account. It is outside the trust equation.
_Avoid_: operating account, management operating account

### Dashboard financial metrics

**Trust total**:
The sum of the current cash-basis book balances of operating trust and security-deposit trust
accounts. It is a fiduciary-cash total and never includes a PM operating account.
_Avoid_: total bank cash, all bank balances

**Owner operating balance**:
An owner's cash-basis owner-equity balance before a prospective management fee or reserve floor is
applied. It can be positive without all of it being available for disbursement.
_Avoid_: owners payable, balance available for disbursement

**Available to disburse**:
For one owner, the positive remainder after the management fee computed on the current owner
operating balance is deducted and the configured reserve is retained. The dashboard total is the sum
of those positive per-owner remainders; it is a present-tense preview, not a payable account.
_Avoid_: owners payable, positive owner balance

**Tenant payments received**:
The net cash movement into trust accounts from `PaymentReceived` events during a calendar period,
including linked reversals in the period. Receipt date controls the period, so a payment against a
prior-period charge counts when received. Owner contributions and other owner-equity credits do not
count. This is not charge-attributed rent collected: one payment may settle rent, fees, or excess
prepayment.
_Avoid_: collected rent, owner income received

**Scheduled rent**:
The rent-run billing amount for leases effective during a calendar month, including the
same actual-days proration used by the rent run. It is a billing baseline, not a collection target or
a denominator for tenant payments received.
_Avoid_: collected target, expected collections

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
The tenant-level receivable and liability relationship attributed through the lease effective on a
financial event's accounting date to one unit, property and owner. Pending leases never supply
financial attribution, and posted attribution remains historical.
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

**Run confirmation**:
The operator's decision to execute a previewed run over a selection of targets, and the one
transaction that carries it out, recording every resulting outcome. It names the whole act — not a
step within it. Until 2026-08-09 the word also named the method each run type used to do its own
posting, so it meant two things one call apart; it now means only the first. The noun carries `run`
because the product confirms other things too — bank-statement matches (`Confirm & clear`) and MFA
enrollment — and those uses are correct: `confirm` is an ordinary verb qualified by its object, and
only the bulk-run act has a reserved noun.
_Avoid_: confirm as a bare noun (it does not say which act), commit (that is the transaction's own
word), submit, apply, execute

**Run plan**:
What a run intends to do for each selected target, decided before anything is posted: post this, or
record this target as skipped or excluded for this reason. It is per-run-type knowledge and nothing
else — it holds no loop, no posting and no persistence, and it is what a run type contributes beyond
its preview.
_Avoid_: batch (a plan is not sized or chunked), work list, queue, job

### Reports

**Report catalog**:
The fixed set of reports the product offers. Each entry names the report, the category it is filed
under and the filter controls its builder presents. It is authored in source, identical for every
organization, and never edited by a property manager.
_Avoid_: report list, report registry, saved reports (nothing is saved)

**Filter control**:
One filter the report builder offers for a given report — an owner, a property, a bank account, a
period. A report offers only the controls its catalog entry declares, and each control is named for
the query parameter it sets. Declaring a control decides what a property manager can narrow a report
by; it does not decide what the server will accept.
_Avoid_: accepted filter (nothing rejects one), filter key, query param

**Accounting basis**:
Cash or accrual — which of the two views of the same journal a figure is drawn from. Every figure the
product shows is on exactly one basis, and which one is always stated rather than implied, because
the same period reads differently under each.
_Avoid_: mode, view, accounting method

### Statement delivery

**Statement artifact**:
The immutable rendered statement for one owner, period and basis — the bytes an owner is entitled to
receive. Rendering the same owner and period again produces a different artifact, not a new version of
this one.
_Avoid_: statement (that is the assembled view), delivery record, PDF

**Delivery attempt**:
One request to send one statement artifact to one destination. A second send of the same artifact is a
second attempt, never a change to the first.
_Avoid_: delivery, send, delivery record

**Delivery event**:
One recorded fact about a delivery attempt — that it was queued, accepted, delivered, bounced or
failed. Events are appended and never corrected in place; an attempt's whole history is its events.
_Avoid_: state transition, status change, delivery status

**Delivery status**:
The kind of an attempt's latest recorded event, computed rather than stored. Ordering is by the
recorded sequence, not by the timestamp the reporter supplied.
_Avoid_: delivery state, current state (nothing stores one)

**Provider accepted**:
The email provider took the message for delivery. It says nothing about whether the recipient received
it, and it is never called _sent_ — that word claimed the recipient's side while meaning only this one.
_Avoid_: sent, delivered, successful

**Delivered**:
The recipient's mail server accepted the message. This is the only outcome that speaks for the
recipient's side.
_Avoid_: sent, received, read

**Retry**:
A new delivery attempt against an artifact that was already issued, optionally to a corrected
destination. The artifact is not re-rendered, so a retry always carries the same figures as the attempt
it follows.
_Avoid_: resend (ambiguous between this and re-issuing), redelivery, reattempt of an event
