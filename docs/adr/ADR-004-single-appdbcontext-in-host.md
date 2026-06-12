# ADR-004: One AppDbContext, owned by the host

- **Status:** Accepted
- **Date:** 2026-06-12
- **Deciders:** Engineering

## Context

In a modular monolith, each module could own its own `DbContext`, or the host could own a single
context that discovers entity configurations from the modules. Multiple contexts complicate
cross-module transactions, the single `SET LOCAL app.org_id` per-request transaction (the RLS
boundary), and EF migrations. We are one database and one transaction per request.

## Decision

A **single `AppDbContext`** lives in `LeaseBook.Web/Persistence/`. It discovers
`IEntityTypeConfiguration` implementations from each module assembly (plus the host) via
`ApplyConfigurationsFromAssembly`. Migrations live in the host. Modules contribute mappings and
query through the context / scoped connection; they do not each carry their own context.

## Consequences

- One transaction per request trivially spans all modules and carries the RLS org context.
- A single migrations history and one place to apply the snake_case / `timestamptz` / RLS
  conventions.
- Module data models are co-located in one context — acceptable while the monolith is not being
  extracted.

## Revisit trigger

First time we extract a module into a separate service (its own database/transaction boundary) —
that module then gets its own context and migration history.
