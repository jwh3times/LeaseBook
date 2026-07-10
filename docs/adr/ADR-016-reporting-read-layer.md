# ADR-016: Reporting read layer — Approach C: Accounting owns the statement engine

- **Status:** Accepted
- **Date:** 2026-06-22
- **Deciders:** Engineering

## Context

M5 introduces owner statement generation. Three approaches were considered:

**A. Reporting module queries the journal directly.**
Breaks ADR-007's cross-module boundary. The per-basis filter (`basis IN (@b,'both')`), PM-income
exclusion (`account_class = 'owner_equity'` + `owner_id`-scoped), and the section tie-out are
trust-correctness rules that live in Accounting. Scattering them into a Reporting query produces a
second implementation of the journal read rules that can diverge.

**B. A denormalized read-model schema (statement rows pre-materialized into a Reporting table).**
Durable read models introduce an eventual-consistency gap between the journal and the statement; any
bug in the materialization path silently produces wrong figures. At the M5 scale (≤ 300 units, ~23
owners, monthly generation) the query is fast from the live journal. This option deferred — if the
query becomes a bottleneck under a much larger dataset, it gets its own ADR at that time.

**C (chosen). Accounting owns the categorized statement query + the structural tie-out; a thin
cross-module port (`IOwnerStatementData`) lets Reporting compose and present the result.**
The existing ADR-007 host-adapter pattern contains the cross-module seam. All financial math stays
in Accounting; Reporting receives a ready-to-render `OwnerStatement` value (sections, beginning,
ending, tie-out). The sole cross-schema reads that belong in a reporting layer — owner/property
display names, branding, reconciliation report snapshots — are correctly placed in Reporting
because they are presentation concerns that do not compute any financial figure.

## Decision

**Accounting owns the statement engine.** The `GetOwnerStatementData` query handler runs two SQL
reads over `journal_lines` (in-period owner-equity movement + beginning balance), groups lines into
sections via `StatementSectionMap`, and computes the structural `StatementTieOut` — all within the
Accounting module's boundary, touching no other module's tables.

**The port is batch-shaped (ADR-007 rule).** `IOwnerStatementData.GetAsync` takes
`IReadOnlyList<Guid> ownerIds` and returns `IReadOnlyDictionary<Guid, OwnerStatement>`. Statements
are per-owner documents (not list figures), but the batch signature means the 23-owner monthly run
is one handler invocation — one query pair, not N round-trips. A single-owner call is a one-element
list. This is the minimum honoring ADR-007's batch-reads requirement.

**The host adapter (`OwnerStatementDataAdapter`) wires the port to DI**, delegating to the handler
via `ISender` on the ambient RLS-bearing transaction, exactly as the other adapters do.

**What crosses the schema in Reporting (names, branding, reconciliation snapshots) vs. what does not
(all owner-facing financial math).** Reporting may join the directory tables for display names and
the banking tables for reconciliation report metadata — presentation data with no correctness
invariants. It must never compute its own owner equity figures or replicate the per-basis filter.
If Reporting ever needs a financial figure that Accounting doesn't expose, the fix is to extend the
Accounting port, not to add a cross-module SQL join.

**`UncategorizedEventException` is the exhaustive-map guard.** Every event type that can post an
`owner_equity` line must appear in `StatementSectionMap`. A new event that posts to owner equity but
has no section entry will throw at runtime (and in the property-based test suite) rather than
silently dropping off a statement.

## Consequences

- **Trust-read rules stay in Accounting.** Section categorization, the per-basis filter, and the
  PM-income structural exclusion live in one place; Reporting cannot compute them wrong.
- **Tie-out is testable and structural.** The `Variance == 0` assertion in the property-based suite
  (`StatementInvariantTests`) proves — over random event sequences — that the categorical sum equals
  the raw sum. Any categorization or sign error surfaces as a failing test.
- **The adapter is thin and stable.** Changing the statement SQL touches only Accounting's handler;
  the `IOwnerStatementData` interface and Reporting's consumer are untouched.
- **Cost accepted:** the statement is computed on demand from the live journal. At the anticipated
  scale this is fast. If generation time becomes a user-visible problem, a pre-computed read model
  (Approach B) can be introduced under its own ADR without changing the port interface.

## Revisit trigger

If statement generation (the `GetOwnerStatementData` handler) measurably contributes to page-load
time at the anticipated Pro-tier scale (~300 units, ~23 owners), or if a denormalized read-model
schema is needed for another M5+ feature, re-evaluate Approach B and record a new ADR.
