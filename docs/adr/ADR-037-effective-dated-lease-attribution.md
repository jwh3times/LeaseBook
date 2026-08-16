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
commands resolve attribution on their explicit accounting date; current Directory reads use today's
UTC date; rent scheduling uses the selected period.

Non-pending terms may not overlap for the same organization and tenant or for the same organization
and unit. Commands reject conflicts for useful feedback, while PostgreSQL exclusion constraints over
inclusive `daterange`s close concurrent-write races. Pending terms may overlap, but activation must
pass the same constraint. Azure PostgreSQL allowlists `btree_gist`, which supplies equality operators
for the exclusion constraints.

Journal dimensions remain immutable historical attribution. A later lifecycle-status or term change
does not rewrite a posted entry.

## Consequences

Sequential rows may both retain the `active` lifecycle status without competing on any date, and an
`ended` lease remains usable for historical attribution inside its term. Open-ended terms block all
later non-pending terms until bounded. A renter with simultaneous occupancies still requires distinct
tenant financial accounts because Accounting does not persist per-lease receivable or liability
allocation.

## Revisit trigger

Revisit when one tenant financial account must span simultaneous occupancies. That requires explicit
persisted allocation for receivables and liabilities before relaxing either overlap constraint.
