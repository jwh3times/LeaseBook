# Handoffs machine config

How the Handoffs folder reaches this machine. It belongs to the machine, not to any repository, so
one file serves every repository's handoff and lets-go:
`~/.config/handoffs/config` (`$XDG_CONFIG_HOME/handoffs/config` when that is set). It holds
shell-style `KEY=value` lines; an environment variable of the same name overrides a line.

| Key | Value |
| --- | --- |
| `HANDOFFS_TRANSPORT` | `folder`: the folder is the whole transport. A sync client (the Proton Drive desktop app, Google Drive for desktop) moves it, or nothing does and handoffs stay on this machine. `proton-cli`: no sync client; the folder is a local mirror that `handoff-map.mjs` pulls and pushes through the `proton-drive` CLI. |
| `HANDOFFS_DIR` | The local Handoffs folder: the synced folder, or the mirror. |
| `HANDOFFS_REMOTE` | `proton-cli` only: the cloud folder. Default `/my-files/Documents/Handoffs`. |

## Checking and completing it

```bash
node .agents/skills/handoff/handoff-map.mjs config
```

- **`configured: true`** — use it; nothing to do.
- **`configured: false`** — the output names the `problems` and carries a `proposal` with its
  `evidence`: a synced Proton Drive Handoffs folder found under the home directory, or the
  `proton-drive` CLI on `PATH` (with an exported `HANDOFFS_DIR` reused as the mirror). Show the
  user the proposal and its evidence, apply their corrections, then write it:

  ```bash
  node .agents/skills/handoff/handoff-map.mjs config --write --transport <transport> --dir <folder>
  ```

  `--remote <cloud folder>` is accepted for `proton-cli`. The step is complete when the output
  shows `configured: true`.
- **When no user can answer** (running as a subagent, or in a non-interactive run), write nothing:
  stop and report that this machine has no handoffs config.

## Transport failures

- A command that fails with "needs a login" means the `proton-drive` session lapsed. The user
  runs `proton-drive auth login` in a real terminal (in Claude Code, typing
  `! proton-drive auth login`); then retry the command.
- `handoff-map.mjs` never creates `handoff_map.json`: the map is shared by every repository. When
  the folder has none, the folder is wrong. Ask the user where the Handoffs folder is.
- `conflicts` in a command's output lists sync-conflict copies of the map beside it: the sync
  client could not merge edits from two machines. Ask the user which copy is current before
  writing the map again.
