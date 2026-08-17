# ADR-038: Derive tenant financial standing and unit occupancy

- **Status:** Accepted
- **Date:** 2026-08-17
- **Deciders:** Jerry Holland

## Context

Directory stored `late` and `prepaid` beside tenant lifecycle values in one status column, even
though ledger activity could make both financial facts true at once. It also stored unit occupancy
beside operational unavailability, allowing a unit row to say `vacant` while a lease was effective.
Those writable snapshots competed with Accounting and effective-dated leases, the authoritative
sources introduced by ADR-034 and ADR-037.

## Decision

Tenant rows store only lifecycle status: `current`, `evicting`, or `past`. Tenant financial standing
is derived as of a date from Accounting's FIFO receivable aging and held prepayments; delinquent
balance and unapplied credit are independent amounts rather than one mutually exclusive label.

Unit rows store only operational availability: `available` or `unavailable`. Occupancy is derived as
`occupied` or `vacant` solely from whether a non-pending lease is effective for the unit on the stated
date. Availability never suppresses or overrides an effective lease.

The data migration maps legacy tenant `late`/`prepaid` values to lifecycle `current`, maps legacy unit
`occupied`/`vacant` values to `available`, and preserves `evicting`, `past`, and `unavailable`. Historical
financial and occupancy truth remains reconstructible from the journal and leases.

## Consequences

A tenant may correctly surface as both delinquent and holding unapplied credit. A leased unit may be
both occupied and unavailable. Directory reads pay for authoritative batch projections instead of
using cheap stored labels, while commands and imports can no longer write financial standing or
occupancy.

## Revisit trigger

Revisit when tenant lifecycle transitions or operational availability themselves need effective dates;
that requires histories rather than replacing either authoritative derived projection.
