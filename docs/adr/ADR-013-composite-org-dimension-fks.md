# ADR-013: Promote the journal-dimension FKs to composite `(org_id, id)`

- **Status:** Accepted
- **Date:** 2026-06-20
- **Deciders:** Engineering
- **Supersedes the "Planned hardening" of:** ADR-008

## Context

ADR-008 bound the five `journal_lines` dimension columns (`owner_id`, `property_id`, `unit_id`,
`tenant_id`, `bank_account_id`) to their directory tables with **single-column** foreign keys, and
explicitly flagged the limitation: a single-column FK proves only that the id exists in *some* org's
row, not that it belongs to the *same* org as the journal line. Postgres referential-integrity checks
**always bypass row-level security**, so the constraint is not itself a cross-org isolation boundary.
ADR-008 named the belt-and-suspenders fix — a composite `(org_id, id)` FK — and **deferred it
deliberately** to "the next milestone that already touches `journal_lines` … not as a standalone
change against a green milestone branch."

M4 is that milestone: it reopens the journal schema (the bank register, clearance state, and
reconciliation all read journal lines on bank accounts), so the rework lands here as WP-01, before
anything builds on the schema.

The deferral was deliberate because the change is **not a lone migration**. The accounting test
harness (`AccountingTestHarness`) satisfied the single-column FKs by seeding every dimension id into
one hidden global "harness directory" org and relying on RI bypassing RLS — one global row per id
served every test org. A composite FK breaks that trick: the line `(orgA, dimId)` now requires the
target row to exist in `orgA`, so the global row no longer satisfies a different test org.

## Decision

**Promote all five `journal_lines` dimension FKs to composite `(org_id, <dim>_id) → (org_id, id)`,
and rework the harness to seed FK targets per test org.**

- Each directory table (`owners`, `properties`, `units`, `tenants`, `bank_accounts`) gains a
  `UNIQUE (org_id, id)` **alternate key** (EF `HasAlternateKey`, so it surfaces in the model
  snapshot) — the target a composite FK requires. `id` alone remains the primary key; the alternate
  key is an additional unique index.
- The migration (`CompositeDimensionFks`) **drops** the five single-column FKs and **re-adds** them
  as composite, authored as raw `AddForeignKey` calls in the migration body — **not** in the EF model.
  The Accounting entities still gain **no navigation properties** (P26/ADR-008): the constraint is a
  database guarantee, not an object-graph relationship, invisible to the model snapshot like the RLS
  policies and GIN indexes. `ON DELETE RESTRICT` is preserved. On a fresh database, migrations run
  before seeding, so `journal_lines` is empty when the constraints change (trivial validation).
- The dimension columns stay **nullable**; with Postgres `MATCH SIMPLE` the FK is enforced exactly
  when the dimension id is non-null (a line with no owner skips the check). `org_id` is `NOT NULL` on
  `journal_lines`, so a non-null dimension always carries a real `(org_id, dim_id)` pair to validate.
- **FKs *into* the immutable journal stay single-column** (P61). New M4 tables that reference
  `journal_lines` (e.g. `bank_line_status.journal_line_id`) use a plain FK to `journal_lines(id)`:
  the journal PK is globally unique and generated in-org, and the row carries `org_id` under RLS, so
  no composite is needed. Composite FKs are the directory-dimension rework only.

### Harness rework

- Bank ids become **per-org**: `bank_accounts.id` is globally unique, so a fixed bank id cannot be
  seeded into more than one org. `ProvisionedScopeAsync` generates three fresh bank ids per scope,
  provisions the chart and seeds the directory `bank_accounts` rows with them, and records them on
  the `OrgScope` (`TrustBankId`/`DepositBankId`/`OperatingBankId`). Tests read the ids from the scope.
- `EnsureDirectoryAsync` seeds FK-target rows **into the scope's own org** (under that org's RLS
  context), replacing the hidden global org. Per-org sentinel owner/property parents are minted only
  when properties/units are actually seeded, so a dim-less org stays free of placeholder rows.
- Test classes that shared **fixed** dimension-id constants (`owner = …e1`, etc.) now mint **fresh
  ids per test instance** (`UuidV7.NewId()`). Fixed ids reused across classes collided on the global
  PK once seeding became per-org; fresh ids are unique by construction.

## Consequences

- **The FK now enforces org-correctness, not just existence.** A journal line in one org provably
  cannot reference a directory row from another org. A new test
  (`CompositeDimensionFkTests`) plants a line in org A pointing at org B's bank and asserts the
  `23503` foreign-key violation — it fails against the old single-column constraint and passes
  against the composite one.
- **Zero journal churn.** The constraints are additive over an already-valid graph; the M1 golden
  figures stay byte-identical and `check-invariants --org demo` still exits 0. The full
  golden/invariant/property/integration gate is re-run and green on the reworked harness.
- **The composite FKs remain DB-only** (no navigation properties), so a future migration touching
  these columns must re-state the FK by hand — consistent with ADR-008 and the RLS/GIN precedent.
- **Cost accepted:** five extra unique indexes (one per directory table) and a slightly larger
  harness surface (per-org bank ids on `OrgScope`). Both are cheap and localized.

## Revisit trigger

If a directory table's primary key ever changes shape, or a new journal-dimension column is added,
re-state its composite FK in the same migration. ADR-008's `is_system` aggregate-row mechanism and
its own revisit trigger are unaffected by this change.
