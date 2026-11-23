<!-- Generated from settings/guard.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: guard

`docs/agents/guard.md` — the commands and paths this repository's guard stops, on top of the rules
it applies everywhere. `scripts/guard.mjs` runs before every shell command and file edit an agent
makes, in a session, a subagent or a scheduled run, so a rule written here holds whether or not
the agent read it. Each setting below is a `##` heading in that file; other headings and prose
outside them are ignored.

Every value is `none` (or a `discover:` value, which sets no rules) or a bulleted list, one rule
per bullet:

```markdown
- `<regex>` — <reason>, continued on indented lines when long.
```

A bullet may start with `*`, and its dash may be an em dash, an en dash or a hyphen. The regex is
JavaScript's, and holds no backtick. A malformed bullet or regex fails `node scripts/guard.mjs --check` and
`node scripts/sync-agents.mjs --check`, naming the line; until it is fixed, the guard applies only
its universal rules and says why in a warning on every call. The guard is accident prevention, not
a sandbox: a rule catches a command as written, never every way to reach the same effect.

## Deny commands

- **Read by:** `scripts/guard.mjs`
- **Value:** `none`, or rules for commands that are never run here, each with what it destroys. A
  regex is matched against each simple command with its quotes removed, the prefixes that only
  run it (`VAR=x`, `env`, `sudo`) dropped, the program's path and `.exe` dropped, and its words
  joined by single spaces: `npx supabase db reset` is matched as written, so an unanchored
  `supabase db reset` catches it. A match is denied, and the reason goes to the agent.
- **Default:** none.
- **Discover:** the **Never run** commands under `Dev stack` in `docs/agents/workspace.md`, and
  the destructive commands the README's local-development section warns about (volume deletes,
  database resets, deploys that only CI runs).

## Ask commands

- **Read by:** `scripts/guard.mjs`
- **Value:** `none`, or rules for commands that run only on the user's say-so each time, matched as
  `Deny commands` are. A match asks the user, showing the reason.
- **Default:** none.
- **Discover:** the commands `docs/agents/workspace.md` and the README say need care rather than
  never running (a push to a linked remote database, a stack stopped in a way that loses data).

## Protected paths

- **Read by:** `scripts/guard.mjs`
- **Value:** `none`, or rules for paths that are never deleted or overwritten without the user's
  word. A regex is matched against the path relative to the repository root, with forward slashes
  (a folder also with a trailing `/`), case-insensitively on Windows. The guard asks before a Write
  that replaces such a file, and before a command that removes, moves or writes one: `rm`, `mv`,
  `cp`, `tee`, `git rm`, `Remove-Item`, `Copy-Item`, `Set-Content`, `Out-File` and the like, and
  a `>` or `>>` redirect. A removal also asks when it takes a protected path with it: the
  repository root or a folder above it, and a folder an anchored rule's literal start lies inside
  (`rm -rf docs` for `^docs/agents/x$`); an unanchored rule is checked only against the path
  named. Edits in place pass.
- **Default:** none.
- **Discover:** `Never-remove paths` in `docs/agents/workspace.md`, anchored: `^\.env(\..*)?$`,
  `^private/$`.
