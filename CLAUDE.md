# CLAUDE.md

Claude Code adapter for this repository. `AGENTS.md` is the canonical engineering contract — it owns
repository state, verification rules, commands, architecture boundaries, trust-accounting and tenancy
invariants, working conventions, and documentation ownership. It is imported below; if the import does
not resolve, read [`AGENTS.md`](AGENTS.md) directly before starting. Where this file conflicts with it,
`AGENTS.md` wins.

@AGENTS.md

## Claude Code Integration

Everything above is the cross-agent contract. This section is the only Claude-specific part.

- The specialist guidance files listed in **Domain Guidance Files** are executable subagents here, each
  named for its file basename — `dotnet-api`, `react-frontend`, `postgres-specialist`,
  `trust-accounting`, `code-reviewer`, `azure-infrastructure`, `docs-updater`. Dispatch the one that
  owns the domain rather than only reading its file.
- The `/ship` skill runs the pre-push documentation-drift check: it invokes `docs-updater` for the
  docs it owns and flags private-roadmap WP drift. Treat its report as a prompt to inspect the owning
  document, not as permission to update every mentioned file.
- Invoke `docs-updater` before a push or PR when source behavior, commands, ports, architecture,
  business events, or user workflows changed.
- Do not rely on these session-scoped checks as guarantees: they do not run in Codex, CI, Dependabot,
  or manual editing sessions, so repository CI and the contributor checklist must carry the
  enforceable guarantees.
