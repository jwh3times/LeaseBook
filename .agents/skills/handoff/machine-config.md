# Handoffs machine config

How the Handoffs folder reaches this machine, and what this machine is called. It belongs to the
machine, not to any repository, so one file serves every repository's handoff and lets-go; what-next
reads it too, without writing it:
`~/.config/handoffs/config` (`$XDG_CONFIG_HOME/handoffs/config` when that is set). It holds
shell-style `KEY=value` lines; an environment variable of the same name overrides a line.

| Key | Value |
| --- | --- |
| `HANDOFFS_TRANSPORT` | `folder`: the folder is the whole transport. A sync client (the Proton Drive desktop app, Google Drive for desktop) moves it, or nothing does and handoffs stay on this machine. `proton-cli`: no sync client; the folder is a local mirror that `handoff-map.mjs` pulls and pushes through the `proton-drive` CLI. |
| `HANDOFFS_DIR` | The local Handoffs folder: the synced folder, or the mirror. |
| `HANDOFFS_REMOTE` | `proton-cli` only: the cloud folder. Default `/my-files/Documents/Handoffs`. |
| `HANDOFFS_MACHINE` | The **machine label**: a short lowercase slug the user picks for this machine (`fedora`, `windows-desk`). `handoff` writes it into each document's **Where you are** as "Written on", so `what-next` and `lets-go` can say where a handoff was written. It is information only: a handoff resumes on either machine. |

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

### The machine label

`configured` does not depend on the label: a machine without one works as before, and its
documents carry no "Written on" line. Only the `handoff` skill asks for it, before it writes a
document; `what-next` and `lets-go` read it as they find it.

- **`machine` is set** — nothing to do.
- **`machine` is null** — propose `machineProposal` (made from the hostname) and let the user
  rename it, then write it; the rest of the config is kept:

  ```bash
  node .agents/skills/handoff/handoff-map.mjs config --write --machine <label>
  ```

  The step is complete when the output's `machine` is the label. An exported `HANDOFFS_MACHINE`
  overrides the file, so the output shows that value instead: tell the user to change or unset it
  where it is exported. A `machineProblem` means the config holds a label that is not a lowercase
  slug: propose a corrected one the same way.
- **When no user can answer** — write nothing, carry on without the label, and say in the report
  that it is unset.

## Transport failures

- A command that fails with "needs a login" means the `proton-drive` session lapsed. The user
  runs `proton-drive auth login` in a real terminal (in Claude Code, typing
  `! proton-drive auth login`); then retry the command.
- `handoff-map.mjs` never creates `handoff_map.json`: the map is shared by every repository. When
  the folder has none, the folder is wrong. Ask the user where the Handoffs folder is.
- `conflicts` in a command's output lists sync-conflict copies of the map beside it: the sync
  client could not merge edits from two machines. Ask the user which copy is current before
  writing the map again.
