# ADR-007: Cross-module reads go through consumer-owned ports, not shared SQL

- **Status:** Accepted
- **Date:** 2026-06-12
- **Deciders:** Engineering

## Context

Through M1 the modular monolith had only _host → module_ calls (the host references every module;
modules reference `SharedKernel` only — enforced absolutely by `ModuleBoundaryTests`). M2 introduces
the first _module → module_ data dependency: the Directory index screens must show financial figures
the Accounting module owns (a tenant's balance, an owner's operating/deposit equity), and creating a
bank account in Directory must provision the matching chart-of-accounts account in Accounting.

There are two tempting shortcuts, both of which we reject:

1. **A cross-schema raw-SQL JOIN** — a Directory query handler reading `journal_lines` directly. This
   is invisible to the architecture tests (they analyse assemblies/namespaces, not SQL strings), so the
   boundary is breached without any guard noticing. Worse, it re-implements _how the journal is read
   correctly_ — the per-basis filter (`basis IN (@b,'both')`; `both` lines count once per basis) and
   the structural PM-income exclusion (no `pm_income` line reachable by an owner-facing query) — outside
   the one module that owns those invariants. Trust correctness is the product; scattering its read
   rules across modules is how a figure on a list silently disagrees with the ledger, or PM income
   leaks into an owner number.
2. **Putting the contract in `SharedKernel`** — `SharedKernel` today is pure cross-cutting primitives
   (Money, ids, CQRS, tenancy). Adding `IAccountingReadModels` there makes it the first domain-specific
   surface in the shared kernel, a smell that compounds as every module publishes contracts.

At our scale (≤ 300 units, Pro-tier ceiling) the performance difference between a single JOIN and a
port that runs an extra batched query is negligible — this decision is about correctness, coupling, and
maintainability, not throughput.

## Decision

**Feature modules never read another module's tables or data directly** — no cross-module SQL, no
cross-module LINQ, no referencing another module's entity types. A cross-module read goes through a
**consumer-owned port implemented by a host adapter** (Dependency Inversion):

- The **consuming** module declares the interface it needs in its _own_ `Contracts` namespace
  (e.g. `Directory.Contracts.ITenantFinancials`) and depends only on that abstraction.
- The **host** (which legitimately references every module) implements the port with a thin adapter
  that delegates to the producing module — dispatching the producer's existing read query via
  `ISender` — and registers it in DI. `SharedKernel` stays pure; the producing module is untouched.
- **Ports expose batch reads, never per-id reads** (`BalancesAsync() → Dictionary<Guid,decimal>`, not
  `BalanceAsync(id)`), so the consumer merges in memory in one round-trip and never N+1s.
- Adapters are DI-scoped and dispatch on the ambient `ISender`, so the read rides the request's
  RLS-bearing transaction (`SET LOCAL app.org_id`) — no new connection, no context leak.

**Within a module, raw SQL is fine** — `db.Database.SqlQuery<T>` for the module's _own_ analytical
reads (window functions, `FILTER`, trigram) crosses no boundary. Prefer EF LINQ for simple
intra-module reads (type-safe, the org query filter applies for free); reserve `SqlQuery<T>` for
queries LINQ cannot express.

**The one explicit exception: a dedicated reporting/read layer.** A module whose job _is_ cross-cutting
reporting (the M5 statement/report engine; a future read-model/reporting schema) may read broadly
across the schema on purpose — that is a named, deliberate place for cross-entity reads, not an ad-hoc
JOIN buried in a feature handler. Such a layer records its own ADR when it arrives.

## Consequences

- **Trust-read rules stay in Accounting.** Every consumer gets a number it cannot compute wrong; the
  per-basis and PM-income invariants live in one place. This is the decisive win for a correctness
  product.
- **Refactor-safe and testable.** Changing the journal schema touches only Accounting's query + the
  adapter; consumers see a stable, compile-checked interface and can be unit-tested against a fake
  port. Each module read keeps its own `cqrs.<Name>` telemetry span.
- **Cost accepted:** an interface + DTO + host adapter + DI line per seam — more moving parts than one
  JOIN. At the handful of M2 seams this is small; the host adapter is the composition root doing its
  job. The "consumer owns the abstraction it needs" inversion is intentional (DIP), not backwards.
- **Not statically enforceable in full.** The assembly/namespace half is enforced by
  `ModuleBoundaryTests`; the "no cross-module raw SQL" half cannot be (NetArchTest can't see SQL
  strings) — it is a code-review rule, called out in `CLAUDE.md`.

## Revisit trigger

If host-adapter glue accumulates to the point of friction (many near-duplicate ports across modules),
or a module is actually extracted to its own process, graduate to **separate `*.Contracts` assemblies**
that modules may reference directly (refining the boundary test to allow `.Contracts` references while
still forbidding implementation references) — the heavier pattern this ADR deliberately defers.
