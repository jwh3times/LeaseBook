# MCP servers

- **Audience:** Coding agents and maintainers
- **Status:** Living policy
- **Owner:** Maintainers
- **Last reviewed:** 2026-11-27

The shared MCP servers: how they are configured and what each is for.

## Configuration

- The shared servers are in `.mcp.json`, which Claude Code asks you to approve on first use.
  `.codex/config.toml` is generated from it by `node scripts/sync-mcp.mjs`, and Codex loads it once
  you trust the project in your own Codex config. CI fails a stale copy.
- Entries carry no `env` or `headers`, so a server that needs a key stays in your own config.
- Nothing updates the pinned `@playwright/mcp` version: bump it by hand.

## The servers

- `microsoft-learn` covers .NET, ASP.NET Core, EF Core and Azure; `context7` covers the SPA and
  third-party libraries.
- `playwright` drives a browser against the running app (`:5373`) to check a change or a UX click
  budget. It does not replace e2e coverage of a budgeted flow.
- There is deliberately no database server: a session missing `app.org_id` or `app.persona` reads
  zero rows and reports them as fact. Use `./scripts/dev.ps1 psql` and set both.
