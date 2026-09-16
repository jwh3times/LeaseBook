---
name: handoff
description: Hand this session to another machine — write a handoff doc to Proton Drive, register it in handoff_map.json, then run end-session.
argument-hint: "What will the next session be used for?"
disable-model-invocation: true
---

# Handoff

Hands the session to the other machine (this Windows PC or the Fedora PC). The doc travels through
Proton Drive; code travels only through what is **pushed**. `/lets-go` on the other machine resumes it.

**Announce at start:** "I'm using the handoff skill to hand this session off."

The map is read and written only through `scripts/handoff-map.mjs` in this skill's directory — run it
from the repo root so it can name the repo from the `origin` remote. It finds the Proton Drive
`My files/Documents/Handoffs` folder itself; if it cannot, it says so and `HANDOFFS_DIR` overrides it.

## How the Handoffs folder reaches this machine

Two transports, decided by what is installed:

- **Desktop client** (Windows) — the Proton Drive client keeps
  `~/Proton Drive/<account>/My files/Documents/Handoffs` in sync on its own. Writing into that folder
  is the whole sync; the CLI steps below are skipped.
- **CLI mirror** (Fedora, where no client exists) — `proton-drive` (the Proton Drive CLI) is on
  `PATH` and `HANDOFFS_DIR` names a local mirror folder. Nothing syncs by itself: each **Pull** and
  **Push** block below is run explicitly, and the cloud folder is always
  `/my-files/Documents/Handoffs`. A CLI reply of `You need to login first` means the user must run
  `proton-drive auth login` first; that is an interactive step, not one for the agent.

Decide once at the start: `command -v proton-drive` succeeds **and** the desktop client's folder is
absent means CLI mirror; otherwise desktop client.

## Steps

### 1. Audit unmerged work and alert

Check **both** trees — the public checkout and, when present, `private/`:

```
git fetch origin --prune
git status --short --branch
git log --oneline origin/main..HEAD
git for-each-ref refs/heads --format='%(refname:short) %(upstream:short) %(upstream:track)'
gh pr list --author @me --state open --json number,title,headRefName,url

git -C private fetch origin --prune
git -C private status --short --branch
git -C private log --oneline origin/main..HEAD
```

Work is **unmerged** when any of these hold: uncommitted or untracked changes, a current branch other
than `main`, commits not on `origin/main`, a local branch with no merged PR, or an open PR. This repo
squash-merges, so a branch that looks unmerged may be drained — prove it with the tree comparison in
`end-session` step 5 before calling it merged.

Before writing anything else, **alert** the user with a `⚠ Unmerged work` block listing every item,
split into:

- **Pushed** — reachable from the other machine.
- **Local only** — will not reach the other machine: uncommitted changes, unpushed commits, branches
  with no upstream.

If anything is local only, ask whether to commit and push it before continuing; pushing is the user's
call. Whatever stays local goes into the doc's state section by name.

Done when every branch and dirty path in both trees is classified, or the block says
"All work is merged to `main`."

### 2. Write the doc

Summarise the session so a fresh agent on the other machine can continue the work. If the user passed
arguments, they describe the next session's focus — tailor the doc to it.

Sections:

- **Focus** — what the next session is for.
- **State** — branch, PR, and push state from step 1, including anything left local only.
- **Next steps** — ordered, the first one concrete enough to start without asking.
- **Suggested skills** — skills the next agent should invoke, and when.
- **References** — issues, PRs, ADRs, specs, commits, and paths.

Reference other artifacts (specs, plans, ADRs, issues, commits, diffs, `private/` documents) by path
or URL rather than restating them. Redact secrets, credentials, and personally identifiable
information.

Name it `<repo>-handoff-<YYYY-MM-DD>[-<focus>].md`, where `<repo>` is the repo name in lower kebab
case (`leasebook`) and `<focus>` is an optional short kebab slug. Write it to the session scratchpad
(or the OS temp directory), never the workspace.

### 3. Publish and register

**Pull** (CLI mirror only) — refresh the map before the script reads it, so the entry written on the
other machine is the one this run supersedes:

```bash
mkdir -p "$HANDOFFS_DIR"
proton-drive filesystem download -f remove /my-files/Documents/Handoffs/handoff_map.json "$HANDOFFS_DIR"
```

```
node <this skill's directory>/scripts/handoff-map.mjs publish <doc path>
```

It copies the doc into the Handoffs folder (suffixing `-2`, `-3` rather than overwriting a same-named
file), sets this repo's `Active_Handoffs` entry to it, and stamps `Last_Updated`. If the output's
`previous` is a different file, tell the user that earlier handoff was replaced without being resumed;
its file stays in the folder.

Done when `handoff-map.mjs get` reports this doc as `file` with `exists: true`. If the script fails,
stop here, give the user the scratchpad path of the doc and the error, and skip step 4.

**Push** (CLI mirror only) — the document and the map both leave this machine now:

```bash
proton-drive filesystem upload -f create-new-revision -t \
  "$HANDOFFS_DIR/<file>" "$HANDOFFS_DIR/handoff_map.json" /my-files/Documents/Handoffs
```

`create-new-revision` keeps the cloud's earlier map as a revision instead of trashing it. The push is
complete when the transfer summary lists both files as uploaded (an unchanged file reports as
skipped, which is also complete). A summary with neither means the user's login lapsed — see the
transport section above.

### 4. End the session

Invoke the `end-session` skill and run it to completion. Anything it lands afterwards — closed issues,
memories, pushed `private/` commits — is picked up live by `/lets-go`, which checks the doc against
current state.

### 5. Report

- The published doc path and the map entry, plus any replaced previous handoff.
- The reminder: run `/lets-go` in this repository on the other machine once the document is in the
  cloud folder (the desktop client syncs it within a minute or so; a **Push** puts it there at once).
- The step 1 alert again, updated for anything pushed since — or "All work is merged to `main`."
- The `end-session` report.
