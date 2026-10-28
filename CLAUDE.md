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
  `trust-accounting`, `code-reviewer`, `azure-infrastructure`. Dispatch the one that owns the domain
  rather than only reading its file.
- Skills, including `/ship` and `docs-updater`, are shared with Codex and described under
  **Agent skills**; they are session-scoped, so repository CI and the contributor checklist carry
  the enforceable guarantees.
