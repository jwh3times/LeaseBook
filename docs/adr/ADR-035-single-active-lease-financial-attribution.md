# ADR-035: One active lease supplies tenant financial attribution

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Jerry Holland

## Context

Tenant ledger commands accept a tenant identity and the journal stores tenant-level receivable and
liability balances. The commands resolved owner, property and unit from an unordered first active
lease. If a tenant had simultaneous active leases under different owners, a payment could move cash
equity to an owner chosen by database row order. Adding a lease selector only at the command surface
would not fix this: Accounting has no persisted per-lease receivable or liability balance with which
to guard or allocate the selected posting.

## Decision

Phase 1 supports at most one active lease per tenant. That lease supplies the owner, property and unit
for new tenant-account postings. Pending and ended leases remain valid historical and future records.
Create and update commands reject a second active lease, and a partial unique index on
`lease_lite (org_id, tenant_id) WHERE status = 'active'` closes the concurrent-write race. Reads that
resolve active-lease financial context require zero or one result; they never select an arbitrary
first row.

Journal dimensions remain append-only historical attribution. Ending, editing or replacing a lease
does not rewrite an existing entry's owner, property or unit.

## Consequences

Tenant-level payment, credit, prepayment and deposit rules remain coherent because exactly one lease
can supply current financial attribution. A renter who simultaneously rents more than one unit must
be represented by distinct tenant accounts in Phase 1. Existing databases with duplicate active
leases must resolve that ambiguity before this migration can create the unique index.

We reject both silent first-row selection and a lease-id-only command change: the former guesses with
money, while the latter presents false precision without per-lease receivable and liability state.

## Revisit trigger

Revisit when the product supports one legal renter across simultaneous occupancies. That work must
introduce explicit persisted allocation for receivables and liabilities before relaxing the active
lease cardinality.
