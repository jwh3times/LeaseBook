# CLAUDE.md

This file adapts the repository's canonical agent contract for Claude Code.

## Required Project Guidance

Read and follow [`AGENTS.md`](AGENTS.md) at the start of every task. It owns repository state,
verification rules, commands, architecture boundaries, trust-accounting and tenancy invariants,
working conventions, and documentation ownership. If this file or a specialist instruction conflicts
with `AGENTS.md`, `AGENTS.md` wins.

The gitignored `private/` tree may be absent from a public clone. Follow the public engineering
contract for ordinary fixes and accepted public work. When milestone scope or acceptance depends on
unpublished product, customer, security, or compliance decisions, ask the maintainer for the relevant
private context instead of reconstructing it.

## Specialist Agents

Read the relevant file under `.claude/agents/` before editing its domain:

| Work type                                                                 | Guidance                                 |
| ------------------------------------------------------------------------- | ---------------------------------------- |
| .NET features, endpoints, commands/queries, integration tests             | `.claude/agents/dotnet-api.md`           |
| React components, hooks, design tokens, frontend tests                    | `.claude/agents/react-frontend.md`       |
| Migrations, RLS policies, schema design, Postgres queries                 | `.claude/agents/postgres-specialist.md`  |
| Accounting posting logic, journal entries, trust equation changes         | `.claude/agents/trust-accounting.md`     |
| Pre-merge correctness review                                              | `.claude/agents/code-reviewer.md`        |
| Azure infrastructure, deploy workflows, Key Vault, managed identity, PITR | `.claude/agents/azure-infrastructure.md` |
| Documentation drift and ownership                                         | `.claude/agents/docs-updater.md`         |

Specialist files provide domain examples and checklists. They may narrow a workflow but may not relax
an invariant or module boundary from `AGENTS.md`.

## Claude Code Integration

- The `/ship` skill runs the pre-push documentation-drift check: it invokes `docs-updater` for the
  docs it owns and flags private-roadmap WP drift. Treat its report as a prompt to inspect the owning
  document, not as permission to update every mentioned file.
- Invoke `docs-updater` before a push or PR when source behavior, commands, ports, architecture,
  business events, or user workflows changed.
- Do not rely on these session-scoped checks as guarantees: they do not run in Codex, CI, Dependabot,
  or manual editing sessions, so repository CI and the contributor checklist must carry the
  enforceable guarantees.
