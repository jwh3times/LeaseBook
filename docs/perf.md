# Read-path performance

- **Audience:** Contributors and reviewers
- **Status:** Living performance record
- **Owner:** Maintainers
- **Last reviewed:** 2026-07-19

LeaseBook budgets **p95 < 300 ms** on the three money-critical read paths at the design scale the
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

Exit codes: **0** all paths within budget · **1** at least one p95 missed · **2** the probe could not
run at all (host unreachable, fixture not seeded, login failed).

It measures over the wire, not in process, because the budget is a user-facing promise: the number
has to include routing, authorization, serialization, and the RLS transaction, not just the SQL. It
authenticates exactly the way the SPA does (prime the antiforgery cookie, then echo it as
`X-XSRF-TOKEN`), reads each response body to completion so serialization is counted, and issues
requests serially — this is per-request latency, not throughput under concurrency.

## Measured results

**2026-07-19** — first measurement. Windows 11, local Docker PostgreSQL 18, `n=100`, warmup 10,
`ASPNETCORE_ENVIRONMENT=Development`.

| Read path     | p50     | p95     | p99     | Budget      |
| ------------- | ------- | ------- | ------- | ----------- |
| Tenant ledger | 3.7 ms  | 4.3 ms  | 14.7 ms | ✅ < 300 ms |
| Dashboard     | 22.3 ms | 28.4 ms | 39.4 ms | ✅ < 300 ms |
| Bank register | 18.9 ms | 25.4 ms | 30.0 ms | ✅ < 300 ms |

All three sit an order of magnitude inside the budget, so **no remediation was required** and no
index or query was changed for performance. That headroom is the useful result: it means the
ADR-016 revisit trigger (materializing statement read models) is not warranted at this scale.

Read these as same-machine comparisons, not absolutes. Local hardware, a warm page cache, and a
loopback network all flatter the numbers; their value is detecting a regression against the
previous run on comparable hardware, and confirming the order of magnitude.

Note the tenant ledger is inherently a small read — one tenant's entries — so it does not scale with
org size the way the other two do. The dashboard (all-owner balances across the portfolio) and the
bank register (a paginated projection over the full journal for one account) are the paths that
actually exercise scale.

## When a path misses budget

1. Reproduce it, then get the plan: `EXPLAIN (ANALYZE, BUFFERS)` on the offending query.
2. Fix the access path. The usual remedies are a covering index leading with `org_id` (queries always
   filter by org first) or a window-function rewrite.
3. Do **not** introduce a denormalized cache of ledger state. Tenant ledgers, owner ledgers, bank
   registers, and statements are projections of the journal, never independently maintained state.
   Materializing them would be an ADR-016 revisit and needs its own ADR — not a performance patch.
4. Re-run the probe and update the results table above with the new date and numbers.

## Why this is not a CI gate

Latency on a shared CI runner varies enough that a 300 ms threshold would be flaky, and a flaky gate
gets ignored or removed. Until a deployed environment exists to measure against on stable hardware,
this stays a documented local check. Revisit CI-gating once Track B provides that environment.
