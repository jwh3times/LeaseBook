# ADR-008: Journal-dimension FKs and system aggregate rows

- **Status:** Accepted
- **Date:** 2026-06-13
- **Deciders:** Engineering

## Context

Through M1 the journal's dimension columns (`journal_lines.owner_id`, `property_id`, `unit_id`,
`tenant_id`, `bank_account_id`) were **bare uuids** (P26): the directory tables they conceptually point
at did not exist yet, so the ids — seeded from `DemoIds` — were carried but unconstrained. M2 builds the
directory (owners, properties, units, tenants, bank accounts), so those ids can finally become real
foreign keys, closing the gap where a journal line could reference a dimension that does not exist.

Two facts complicate a naive "add five FKs" migration:

1. **The journal already references synthetic aggregate ids that are not real entities.** The M1 demo
   journal posts owner-equity lines against `AggregateOwners` (the rolled-up equity of the 15 unlisted
   owners the prototype only summarises), and deposit-liability lines against per-owner deposit
   aggregates (`AggDepO1..8`) and an unattributed-deposit bucket
   (`AggregateDepositsUnattributed`). The May statement additionally names two tenants (`TOkonkwo`,
   `TLiu`) that are not in the prototype's `tenants[]`. None of these are real directory rows, yet the
   FKs must hold for every existing journal line.
2. **No journal row may change.** The M1 golden figures reconcile to the cent and are the golden-file
   fixture (CLAUDE.md: "seed data is sacred"). Adding referential integrity must be *additive* — it
   cannot rewrite, re-key, or delete a single posted line, or the golden tests and `check-invariants`
   would shift.

We rejected the alternatives: dropping the dimension ids to satisfy FKs (loses the M1 attribution),
making the FKs reference a nullable "dimension" lookup unrelated to the directory (two sources of truth
for the same entity), or fabricating 15 named owners / extra tenants to back the aggregates (pollutes
the golden fixture with invented PRD data — explicitly forbidden by P40).

## Decision

**Add the five FK constraints at the database level only, and materialise every synthetic id as an
`is_system = true` directory row** so the constraints hold without touching any journal row.

- `journal_lines.{owner_id, property_id, unit_id, tenant_id, bank_account_id}` →
  `{owners, properties, units, tenants, bank_accounts}(id)`, all **nullable** (enforced only when the
  column is non-null) and **`ON DELETE RESTRICT`** (a dimension with journal history cannot be deleted —
  reinforcing the append-only, no-delete posture). Authored as raw `AddForeignKey` calls in the
  `AddDirectory` migration body, **not** in the EF model: the Accounting entities gain **no navigation
  properties**, so the modules stay decoupled (consistent with P39 / ADR-007). The FKs live in the DB
  and the schema, not in either module's object graph.

- The synthetic ids become **system rows** (§C.2): `AggregateOwners` → an `owners` row "All other
  owners"; `AggDepO1..8` and `AggregateDepositsUnattributed` → `tenants` rows; `TOkonkwo`/`TLiu` →
  `tenants` rows (seeded now so the M5 statement set is complete). Every list, search, CRUD and
  `/api/directory` query filters `WHERE NOT is_system`, so these never surface as editable entities. The
  **dashboard hero is the one sanctioned consumer** of the `AggregateOwners` roll-up, relabeled "All
  other owners (15)" so its total ties to the trust total (P40).

- **Ordering guarantees the FKs validate** (M2-E1): on a fresh database, migrations run *before*
  seeding, so `journal_lines` is empty when the constraints are added (trivial validation). The seeder
  then materialises all directory rows — including the system aggregates — *before* replaying the
  journal, so every dimension id already has a target when its line posts. Both steps are idempotent.

## Consequences

- **Referential integrity with zero journal churn.** Every dimension id now provably resolves to a row;
  the M1 golden figures stay byte-identical and `check-invariants --org demo` still exits 0 (the gate
  verifies this). The `is_system` rows are the seam that makes "add FKs without touching the journal"
  possible.
- **One uniform `is_system` flag** on `owners`/`tenants` (and present on `properties`/`units` for
  symmetry, though unused today) cleanly separates real entities from aggregate placeholders, with a
  single review rule — `WHERE NOT is_system` — enforced at every read surface (M2-E2).
- **DB-level-only FKs are invisible to the EF model snapshot** (like the RLS policies and GIN indexes).
  This is intentional: the constraint is a database guarantee, not an object-graph relationship. A
  future migration touching these columns must re-state the FK by hand.
- **Cost accepted:** the aggregate rows are a small, clearly-labelled fiction in the directory tables.
  They are the price of preserving the M1 prototype's summary-level attribution without inventing data.

## Revisit trigger

If the synthetic aggregates ever need to become real entities — e.g. M7's migration toolkit imports the
15 currently-unlisted owners, or a deposit aggregate must be split back into real tenants — promote the
relevant `is_system` rows to real rows in that milestone, re-attributing the journal lines through
*linked reversal entries* (never an in-place update, per the append-only invariant) and updating the
golden fixture deliberately. At that point the `is_system` placeholder for that id is retired.
