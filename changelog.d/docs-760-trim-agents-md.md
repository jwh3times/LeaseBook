### Changed

- **`AGENTS.md` now fits inside Codex's instruction budget** (#760). The file was 35,069 bytes,
  and Codex silently drops project instructions past 32,768 bytes, so Codex sessions never saw the
  end of Working Conventions or any of Agent Operating Notes. It is now about 22,300 bytes, and
  every always-needed rule and invariant still applies. Reference material moved behind one-line
  pointers:
  - the full command reference to `docs/runbooks/local-dev.md`, including a new "Builds, tests and
    the image" section
  - command traps to `docs/agents/local-runs.md`
  - the reasons for the row-level-security and platform-table rules to `docs/architecture.md`
  - the MCP server notes to the new `docs/agents/mcp.md`

  Claude Code, which loads the whole file on every request, now reads about a third less.
- `npm run docs:check` fails when `AGENTS.md` is over 25,000 bytes, measured as UTF-8 with CRLF
  line endings counted as LF (`scripts/check-docs-policy.mjs`). The documentation policy records
  why, and what to trim when a shared-skill sync pushes it over.
