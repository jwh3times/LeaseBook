# ADR-000: Record architecture decisions

- **Status:** Accepted
- **Date:** 2026-06-12
- **Deciders:** Engineering

## Context

LeaseBook is built by a solo developer over a multi-month plan. Decisions that deviate from the
defaults in the build plan, or that future-me (or a first hire, or the trust-accounting attorney
review) will need explained, must be captured where they live with the code rather than in chat
history or memory.

## Decision

We keep lightweight Architecture Decision Records (Michael Nygard format) in `docs/adr/`, one file
per decision, numbered sequentially (`ADR-NNN-kebab-title.md`), using `template.md`. Every
deviation from a build-plan default gets an ADR. Each ADR carries an explicit **revisit trigger**.
ADRs are committed (public repo) and contain engineering rationale only — never pricing, strategy,
or customer detail (those stay in `private/`).

## Consequences

- Decisions are discoverable next to the code and reviewable in PRs.
- A small per-decision writing cost; offset by not re-litigating settled choices.

## Revisit trigger

If ADRs stop being written for genuine deviations (drift back to undocumented decisions), or the
team grows enough to warrant a heavier RFC process.
