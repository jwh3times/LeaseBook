# Domain Docs

- **Audience:** Coding agents and maintainers configuring engineering skills
- **Status:** Living configuration
- **Owner:** Maintainers
- **Last reviewed:** 2026-08-28

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Before exploring, read these

- **`CONTEXT.md`** at the repo root — the settled domain glossary.
- **`docs/adr/`** — read the ADRs that touch the area you're about to work in.

Both exist here. This repository has no `CONTEXT-MAP.md` and should not grow one — see the structure
note below. The `/domain-modeling` skill (reached via `/grill-with-docs` and
`/improve-codebase-architecture`) extends both lazily, when terms or decisions actually get resolved.

## File structure

Single-context repo (this repo):

```
/
├── CONTEXT.md
├── docs/adr/
│   ├── README.md                                  ← the ADR index
│   ├── template.md
│   ├── ADR-001-background-job-scheduler.md
│   └── ADR-041-durable-keyring-and-proxy-trust.md
└── src/
```

ADRs are named `ADR-<zero-padded number>-<kebab-case-title>.md` and numbered sequentially from 001.
`docs/adr/README.md` indexes every accepted record and is kept consistent by `npm run docs:check`.

LeaseBook is a modular monolith (`src/LeaseBook.Modules.*`), but it is one product with decisions
recorded centrally under `docs/adr/` — not a multi-context repo. There is no per-module
`src/<module>/docs/adr/` layer.

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test name), use the term as defined in `CONTEXT.md`. Don't drift to synonyms the glossary explicitly avoids.

If the concept you need isn't in the glossary yet, that's a signal — either you're inventing language the project doesn't use (reconsider) or there's a real gap (note it for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it explicitly rather than silently overriding:

> _Contradicts ADR-0007 (event-sourced orders) — but worth reopening because…_
