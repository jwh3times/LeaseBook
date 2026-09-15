---
name: lets-go
description: Resume this repo's active handoff from Proton Drive's handoff_map.json, then mark it consumed.
disable-model-invocation: true
---

# Let's go

Picks up where `/handoff` left off on the other machine. The handoff doc lives in Proton Drive and
`handoff_map.json` beside it names the active doc per repo.

**Announce at start:** "I'm using the lets-go skill to resume this repo's handoff."

The map is read and written only through `../handoff/scripts/handoff-map.mjs`, relative to this skill's
directory — run it from the repo root so it can name the repo from the `origin` remote.

## Steps

### 1. Look up the handoff

```
node <skills directory>/handoff/scripts/handoff-map.mjs get
```

- `file` is `null` → report "No active handoff for `<key>`." and stop.
- `exists` is `false` → Proton Drive has not synced the doc to this machine yet. Report the expected
  path, leave the map untouched, and stop.

Otherwise read the whole doc at `path`.

### 2. Sync the workspace to it

Update both trees (the public checkout and, when present, `private/`):

```
git fetch origin --prune
git status --short --branch
git -C private fetch origin --prune
git -C private status --short --branch
```

Bring the checkout to the state the doc's **State** section describes: fast-forward `main`, or check
out the branch it names from `origin`. A dirty tree, a diverged branch, or a branch missing from
`origin` (work left local only on the other machine) → stop and ask how to proceed.

Then check the doc's claims against live state: issues since closed, PRs since merged, next steps
already done. The doc is a snapshot and `end-session` ran after it was written, so note every
discrepancy; live state wins.

### 3. Mark it consumed

```
node <skills directory>/handoff/scripts/handoff-map.mjs clear --expect <file>
```

This sets the repo's entry to `null`. It refuses when the map now names a different file — a newer
handoff arrived meanwhile — so stop and report instead of resuming a stale one.

### 4. Proceed

Give the user a short brief: focus, current state, discrepancies from step 2, and the first next step.
Invoke the doc's suggested skills where they fit, then start the first next step. When that step is a
consequential or ambiguous call, confirm it with the user first.
