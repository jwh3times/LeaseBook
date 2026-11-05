# ADR-037: Effective-dated lease attribution and non-overlapping terms

- **Status:** Accepted
- **Date:** 2026-08-16
- **Deciders:** Jerry Holland

## Context

ADR-035 made lifecycle status the selector for tenant financial attribution and enforced one
`active` row per tenant. That prevented ambiguous posting, but it rejected valid sequential lease
records and made a future or expired `active` row look current. It also made an `ended` row unusable
for a backdated event that fell inside its historical term.

## Decision

A lease is effective on a date when it is not `pending` and its inclusive term contains that date.
A lease is effective during a period when that non-pending term overlaps the inclusive period. Money
commands resolve attribution on their explicit accounting date; current Directory reads use the
organization's today (see the amendment below); rent scheduling uses the selected period.

Non-pending terms may not overlap for the same organization and tenant or for the same organization
and unit. Commands reject conflicts for useful feedback, while PostgreSQL exclusion constraints over
inclusive `daterange`s close concurrent-write races. Pending terms may overlap, but activation must
pass the same constraint. Azure PostgreSQL allowlists `btree_gist`, which supplies equality operators
for the exclusion constraints.

Journal dimensions remain immutable historical attribution. A later lifecycle-status or term change
does not rewrite a posted entry.

## Amendment (2026-11-05, #642): the books' calendar is the organization's time zone

"Today" was the UTC date, which a US evening has already left: from about 19:00 or 20:00 Eastern
until midnight it named tomorrow. A void dated that way could land in the next owner statement or
reconciliation month, and the current lease, the dashboard's month-to-date figures and the default
statement month all moved a day or a month early.

Each organization's settings now carry a time zone, one of seven US zones stored as an IANA id
(`America/New_York` by default, then Chicago, Denver, Phoenix, Los Angeles, Anchorage and Honolulu).
An administrator changes it on the Organization card; a change moves no date already posted.
Whenever the server works out "today" or "the current month", it takes the instant from the injected
`TimeProvider` and reads it in that zone. That covers:

- the as-of of a void or a refund-check void when none is given;
- the late-fee run's assessment date and the payment-eligibility date;
- the current lease and occupancy on Directory's reads, and the ownership-transfer date check;
- the default period of a statement, a run preview and a report, and the default as-of of a report.

Accounting, Operations and Payments each declare an `IOrgTimeZone` port, and the host adapts all
three to Directory's setting (ADR-007). Directory reads its own setting.

Money moving in or out cannot be dated after the organization's today: a payment staff record, a
deposit or prepayment collected or applied, held deposits moved with a sale, a void's as-of, a refund
check and its void, and a bank fee, interest or transfer. The refusal is `posting_date_in_future`
(422). Charges and credits may still be billed ahead, and so may every period-dated bulk run. A
processor's receipt is dated by its bank evidence, which the processor's own rules bound against the
UTC date, so it posts through `RecordSettledReceipt`, which carries no such refusal. The
ownership-transfer refusal stays a validation error on the effective date, as it was.

The seven zones must resolve on every host. The chiseled runtime image ships no tz database, and on
Windows `InvariantGlobalization` removes the ICU that maps an IANA id to a Windows one. So the
Dockerfile copies `/usr/share/zoneinfo` from the SDK build stage, which runs the same Ubuntu release,
into the runtime image; a Windows host resolves each zone through its Windows id; and the host
resolves all seven at startup, failing to start rather than dating anything in UTC.

## Consequences

Sequential rows may both retain the `active` lifecycle status without competing on any date, and an
`ended` lease remains usable for historical attribution inside its term. Open-ended terms block all
later non-pending terms until bounded. A renter with simultaneous occupancies still requires distinct
tenant financial accounts because Accounting does not persist per-lease receivable or liability
allocation.

## Revisit trigger

Revisit when one tenant financial account must span simultaneous occupancies. That requires explicit
persisted allocation for receivables and liabilities before relaxing either overlap constraint.
