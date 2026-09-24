# Read-path performance

- **Audience:** Contributors and reviewers
- **Status:** Living performance record
- **Owner:** Maintainers
- **Last reviewed:** 2026-09-24

LeaseBook budgets **p95 < 300 ms** on the four money-critical read paths at the design scale the
architecture targets: roughly 300 units across ~25 owners. This page records how that is measured,
what the current numbers are, and what to do when one misses.

The measurement is deliberately reproducible rather than continuous: a synthetic load fixture plus a
CLI probe you run locally. It is **not** a CI gate — see [Why this is not a CI gate](#why-this-is-not-a-ci-gate).

## The load fixture

`seed --org load` provisions a synthetic org sized to the design ceiling. It is a development
fixture, refuses to run in Production, and is idempotent — re-running it is a no-op.

|                             |                                               |
| --------------------------- | --------------------------------------------- |
| Owners / properties / units | 25 / 40 / 300 (285 occupied)                  |
| Activity window             | 12 months                                     |
| Journal entries / lines     | ~7,700 / ~18,900                              |
| Bulk runs                   | 36 (rent, late fee, disbursement × 12 months) |
| Reconciliations             | 33 finalized; the final month is left open    |

Every figure is produced through the real engine — chart provisioning, an opening balance-forward,
the Operations run engine, business events, and the real reconciliation commands. Nothing is
inserted into `journal_entries`/`journal_lines` directly, so the fixture exercises the same posting
paths production does. The generator is deterministic (a seeded xorshift PRNG, not `System.Random`,
whose sequence is not contractually stable across runtimes), so the fixture's shape is identical on
every machine.

`check-invariants --org load` must exit 0. A load fixture that violates the trust equation is a bug
in the fixture or a real engine find — either way it fails loudly rather than being tuned away.

## Running the probe

The probe drives an **already-running** host over HTTP. Start the database, seed, run the host, then
probe it from a second shell:

```powershell
./scripts/dev.ps1 up
$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- seed --org load
$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web
```

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- perf-probe
```

| Flag          | Default                 | Meaning                               |
| ------------- | ----------------------- | ------------------------------------- |
| `--base-url`  | `http://localhost:5080` | Host to probe                         |
| `--n`         | `100`                   | Timed requests per path, after warmup |
| `--warmup`    | `10`                    | Untimed warmup requests per path      |
| `--budget-ms` | `300`                   | p95 budget; the pass/fail threshold   |

Exit codes: **0** all paths within budget · **1** invalid invocation or at least one p95 missed ·
**2** the probe could not run at all (host unreachable, fixture not seeded, login failed). Option
values are strict: a missing, non-numeric, or out-of-range value is an error rather than a request to
use the default.

It measures over the wire, not in process, because the budget is a user-facing promise: the number
has to include routing, authorization, serialization, and the RLS transaction, not just the SQL. It
authenticates exactly the way the SPA does (prime the antiforgery cookie, then echo it as
`X-XSRF-TOKEN`), reads each response body to completion so serialization is counted, and issues
requests serially — this is per-request latency, not throughput under concurrency.

## Measured results

**2026-07-19** — first measurement. Windows 11, local Docker PostgreSQL 18, `n=100`, warmup 10,
`ASPNETCORE_ENVIRONMENT=Development`.

| Read path       | p50     | p95     | p99     | Budget      |
| --------------- | ------- | ------- | ------- | ----------- |
| Tenant ledger   | 3.7 ms  | 4.5 ms  | 15.5 ms | ✅ < 300 ms |
| Dashboard       | 22.5 ms | 30.4 ms | 32.2 ms | ✅ < 300 ms |
| Bank register   | 18.2 ms | 24.9 ms | 32.5 ms | ✅ < 300 ms |
| Owner statement | 16.8 ms | 21.7 ms | 22.7 ms | ✅ < 300 ms |

All four sit an order of magnitude inside the budget, so **no remediation was required** and no
index or query was changed for performance. That headroom is the useful result: reading from the
live journal is not a bottleneck at this scale.

The owner statement is probed for a specific reason. [ADR-016](adr/ADR-016-reporting-read-layer.md)
chose to assemble statements from the live journal rather than a materialized read model, and its
revisit trigger is worded about statement generation measurably contributing to page-load time at
~300-unit scale. At 21.7 ms p95 it does not, so **that trigger has not fired** and Approach B
(materialization) is not warranted at this scale. The statement is measured for the fixture's last
month of real activity — a quiet period returns zeros and would time a read that is not
representative.

Read these as same-machine comparisons, not absolutes. Local hardware, a warm page cache, and a
loopback network all flatter the numbers; their value is detecting a regression against the
previous run on comparable hardware, and confirming the order of magnitude.

Note the tenant ledger is inherently a small read — one tenant's entries — so it does not scale with
org size the way the others do. The dashboard (all-owner balances across the portfolio), the bank
register (a paginated projection over the full journal for one account), and the owner statement
(assembled from the live journal for a period) are the paths that actually exercise scale.

## Index-overlap measurement

**2026-09-24** — PostgreSQL 18 on the same local Docker topology. The composite-FK hardening added
`(org_id, foreign_id)` indexes beside three earlier single-column FK indexes. Because FORCE RLS adds
the organization predicate to runtime reads, the new indexes appeared able to serve the same access
paths; this check measured that claim before removing the older indexes.

The seeded load organization produced 3,199 `bank_line_status` rows. During the seed, PostgreSQL
recorded 2,841 scans on `(org_id, reconciliation_id)` and zero on the earlier
`(reconciliation_id)` index. The load seed deliberately creates no statement imports, so a second,
rollback-only planner fixture inserted 100 valid imports, 20,000 lines, and 20,000 matches inside the
load organization's RLS transaction, analyzed them, compared plans, then rolled the transaction back.
It was a planner/index experiment, not a product-latency measurement, and persisted no fixture data.

| Predicate                         | Before dropping the single index | Composite-only plan                       | Shared buffers |
| --------------------------------- | -------------------------------- | ----------------------------------------- | -------------- |
| Status by reconciliation          | Single-column index scan         | `(org_id, reconciliation_id)` bitmap scan | 10 → 10        |
| Statement lines by import         | Single-column index scan         | `(org_id, import_id)` index scan          | 6 → 6          |
| Statement match by statement line | Single-column index scan         | `(org_id, statement_line_id)` index scan  | 8 → 8          |

All three predicates retained indexed access with unchanged buffer counts. Absolute sub-millisecond
times improved in the second run, but cache warmth and run order make that incidental; the decision
rests on the equivalent access paths and buffers. At the measured size, the three older indexes used
992 KiB together and added one index update per affected insert. They were removed; the composite
indexes remain both the FK-supporting indexes and the runtime access paths.

## When a path misses budget

1. Reproduce it, then get the plan: `EXPLAIN (ANALYZE, BUFFERS)` on the offending query.
2. Fix the access path. The usual remedies are a covering index leading with `org_id` (queries always
   filter by org first) or a window-function rewrite.
3. Do **not** introduce a denormalized cache of ledger state. Tenant ledgers, owner ledgers, bank
   registers, and statements are projections of the journal, never independently maintained state.
   Materializing any of them is an architectural decision that needs its own ADR — not a performance
   patch. For the statement path specifically, that is the
   [ADR-016](adr/ADR-016-reporting-read-layer.md) revisit trigger.
4. Re-run the probe and update the results table above with the new date and numbers.

## Why this is not a CI gate

Latency on a shared CI runner varies enough that a 300 ms threshold would be flaky, and a flaky gate
gets ignored or removed. Until a deployed environment exists to measure against on stable hardware,
this stays a documented local check. Revisit CI-gating once such an environment exists.
