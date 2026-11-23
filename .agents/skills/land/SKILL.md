---
name: land
description: Land a pull request the user has said to merge — catch it up with main, wait for its checks on the current head, work out why a green PR is blocked, merge it, then confirm its release tag, its deploys, the issues it closed and their board status. Use when the user says "merge it", "land #12", "merge when green" or "merge and complete the follow-ups". Never merges a PR the user's words did not name.
metadata:
  settings: merging releases issue-tracker
---

# Land

Take a pull request from "open and approved by the user" to "merged, released and recorded": its
checks green on the head that merges, its release tagged on the merge commit, its deploys watched,
the issues it closed shut and Done on the board.

**Announce at start:** "I'm using the land skill to merge #<n>."

Run shell snippets in bash (Git Bash on Windows). `.agents/skills/land/land.mjs` reads the PR, its
checks, its release and its deploys, and prints one JSON object; it never writes. Board writes go
through `.agents/skills/end-session/board.mjs`, so `end-session` is installed beside this skill,
and a red check is read by `ci-triage` (step 4), installed beside it too.

`land` never passes `--admin` or `--auto`, never changes a branch rule, never runs, re-runs or
dispatches a deploy or a migration, and never merges without a user to answer it. A blocked merge
is a finding to explain, never a rule to get past.

## 0. Settings

This skill reads `docs/agents/merging.md`, `docs/agents/releases.md` and
`docs/agents/issue-tracker.md`. Follow `.agents/skills/land/settings/README.md` to read them and
complete any that are missing; the definitions sit beside it. Resolve each `discover:` value once,
here, and reuse it below.

Then read the `Guide` in full, every run. It is this repository's own account of merging: reds
that are not the code and their remedies, checks that never gate, and procedures for its own steps.
Where it names a remedy or a procedure for a step below, follow it for that step. It can narrow
the approval in step 1, never widen it.

## 1. Approval

- **The user's words name the PRs.** A number, a link, or "this PR" / "merge it" when the current
  branch has exactly one open PR. When "the PR" could mean more than one, ask which.
- **Approval covers what it names, as it was.** A PR opened while carrying this out, such as a
  follow-up, needs its own approval: open it, wait for its checks, report it ready, and ask. A
  commit someone else pushes to an approved PR also needs a new approval; the heads this run makes
  itself, by its catch-up and by remedies the `Guide` names, do not.
- **"Merge when green" or "merge and complete the follow-ups"** covers every step below for the
  named PRs without asking between them, except where a step says to stop.
- **No user, no merge.** Running as a subagent or non-interactively, stop before step 5 and report.

## 2. Read the PR

```bash
node .agents/skills/land/land.mjs pr --repo "<Public repository>" --pr <n>
```

Keep `headSha` as the head being landed, and `closing` and `mentioned` for step 8. Then go by
`verdict`:

| Verdict | Next |
| --- | --- |
| `merged` | Already merged: take `mergeCommit` to step 6. |
| `closed` | Stop. A Dependabot PR closed by a rebase may have a replacement (`gh pr list --author app/dependabot`); report it. It is a new PR, so it needs approval unless the `Guide` says otherwise, and its diff is read again for packages the first lacked. |
| `draft` | Stop and ask. |
| `conflicts` | A Dependabot PR: step 3, whose `Dependabot catch-up` rebases it. Any other: stop and report. Resolving a conflict is a change to the branch, which a merge approval does not cover; offer it. |
| `behind` | Step 3. |
| `pending`, `failed`, `missing-checks`, `blocked` | Step 4. |
| `ready` | Step 5. |

## 3. Catch up

1. **Let `main` settle first.** With `Scheme: stamp-on-tag`, a merge's stamp commit moves `main`
   about a minute after it, so a branch caught up before then is behind again. When `origin/main`'s
   newest first-parent commit (`git fetch -q origin && git log -1 --first-parent --format='%H %s'
   origin/main`) is a merge, not a `chore(release):` stamp, wait for its release:
   `land.mjs release --sha <that commit> --workflow <Release workflow> --wait 300`, until it is no
   longer `running`.
2. **Run `Catch-up`**, or `Dependabot catch-up` when `dependabot` is true, with the PR's number.
   With `Catch-up: none`, the `Guide` says how the branch reaches `main`.
3. **Wait for the new head:**
   `land.mjs pr --repo ... --pr <n> --moved-from <old head> --wait 300` returns once `headSha` is
   no longer the old head, and answers for the new one, which is now the one being landed. Still on
   the old head when it returns (`timedOut`), run it again rather than the catch-up: a Dependabot
   command can take minutes, or go unanswered, and the `Guide` says how long to wait and what to do
   next.
4. Go to step 4 with the new head.

## 4. Wait for the checks

```bash
node .agents/skills/land/land.mjs pr --repo "<Public repository>" --pr <n> --expect-head <head> --wait 300
```

Run it again while the verdict is `pending` (`timedOut` true), and say what is still running every
ten minutes or so. After an hour, stop and ask. A check in `checks.stuck` is pending though its run
finished: read the job, as the `Guide` says, before waiting longer.

- **`head-moved`** when this run did not move it: someone pushed. Stop and ask (step 1).
- **`failed`:** find out why with the `ci-triage` skill, in a subagent where the harness has one
  (it forks on its own where the harness reads its `context: fork`), told the `Public repository`,
  the PR's number, the head and the names in `checks.failed`; otherwise follow
  `.agents/skills/ci-triage/SKILL.md` yourself. It reads each failing step, log and annotation,
  never re-runs or posts anything, and classes each red: known red, runner/infra, likely flake,
  real failure or unknown. Then act only on what the `Guide` says:
  - **known red:** the `Guide`'s remedy that `ci-triage` quotes (re-run the failed jobs, an empty
    commit, a comment) is applied once; then wait again.
  - **every other class** stops the run: report the check, the failed step, the class and the
    lines that say why, from its diagnosis. A runner/infra red or a likely flake is not a remedy
    by itself: it gets a re-run only when the `Guide` names one for it, which makes it a known red.

  Every red stops it, required or not, unless the `Guide` names that check as one that never
  gates. A red after its remedy stops it too. Never re-run a red in the hope it passes.
  `checks.cancelledDuplicates` are not reds.
- **`missing-checks`:** a required check never reported on this head (`checks.missingRequired`),
  which GitHub shows as BLOCKED with every check green. Apply the `Guide`'s remedy for a workflow
  that never started, or stop and report the checks by name.
- **`blocked`** with every check green and nothing missing: a new code scanning alert, or a rule
  that is not a check. Report each of `codeScanningAlerts` (rule, path and line, link) and stop:
  fixing one is a change to the branch, and dismissing one is the user's decision. With no alerts,
  a CodeQL upload that never registered is the usual cause; the `Guide` names its remedy. Otherwise
  report what `rules` requires and stop.
- **`behind`:** step 3. **`conflicts`**, **`closed`**, **`draft`:** as in step 2.
- **`ready`:** step 5.

## 5. Merge

Right before merging:

- **Every issue the merge will close must be one the PR means to close.** GitHub reads a closing
  keyword anywhere in the body, negated ("does not close #22") or under `Part of`, and
  `unexpectedClosing` lists each such issue. When it is not empty, stop and ask. The fix is an edit
  to the PR body that drops the keyword, which the user approves; then read the PR again.
- **The verdict is `ready` for the head being landed**, from a `land.mjs pr --expect-head <head>`
  run just now.

Run `Merge command` with the PR's number. With `Merge command: none`, the `Guide`'s procedure
lands it, with the approvals it names; take that landing commit's sha as the merge for what
follows.

A merge command that fails or times out may still have merged: one that merges on the server and
fails in its local cleanup is common. Read `land.mjs pr` before anything else. `merged`: go on.
Still open, `ready`, on the same head: retry once. Anything else: stop and report.

Keep `mergeCommit` from that read; every step below finds things by it.

## 6. Release

With a `Release workflow`:

```bash
node .agents/skills/land/land.mjs release --repo "<Public repository>" --sha <merge sha> --workflow <Release workflow> --wait 300
```

- **`running`**, or **`no-run`** in its first minutes: run it again. A `no-run` that lasts means the
  workflow did not start for the merge: report it.
- **`released`:** `main.tags` names the version on the merge commit; with `Scheme: stamp-on-tag`,
  `main.stamps` holds the stamp commit above it. Fast-forward the local `main` once the stamp is
  there (step 10).
- **`untagged`:** the run succeeded and tagged nothing, because `main` moved during it; the next
  merge's run tags both. That is not a failure. When this merge is the last, say it stays untagged
  until the next merge.
- **`failed`:** read the failed job's log and report it. Re-run it only when the `Guide` says the
  cause is one a re-run clears.

## 7. Deploys

For each entry of `Deploys`: a workflow with `land.mjs runs --sha <merge sha> --workflow <file>`,
a `check:` with `--check "<name>"`, and a sentence as it says, read-only. Wait with `--wait 300`
until each is `success` or `failed`. A job of the release workflow's own run was watched in step 6.

Never run, re-run or dispatch a deploy, a migration or any command against a hosted service, even
a failed one: report the failed step and the lines that say why, and the user decides. A hosted
step the `Guide` says the user runs is given to them as exact commands, one per block.

## 8. Issues and the board

- **Read the PR again** (`land.mjs pr`): each issue in `closing` should now be `CLOSED`. One still
  open is reported.
- **Each issue in `mentioned` that was open before the merge must still be open.** One the merge
  closed by mistake is reported, and reopened when the user agrees or the `Guide` says to; reopening
  does not move its board item back, so set that too.
- **The board.** With a `Board`, for each issue the merge closed:

  ```bash
  node .agents/skills/end-session/board.mjs get --board "<Board>" --item <owner/name#N>
  node .agents/skills/end-session/board.mjs set --board "<Board>" --item <owner/name#N> --set "Status=Done"
  ```

  Read it after the close, and set it only when it is not already done: a board's "Item closed"
  workflow sets some items and misses others. When `Board fields` records finished work in another
  field or option, set that one. An issue not on the board is reported, not added.
- **Private work.** When the PR's work came from an issue in the `Private issue repository`, whose
  number never appears in the PR, remind the user that it closes by hand. Under "complete the
  follow-ups", close it with a comment naming the merged PR, and set its board item done the same
  way. Its number stays out of every public place.

## 9. Hook: after merge

Run `Hook: after merge` from `docs/agents/merging.md`.

## 10. The local checkout

Run `git fetch --prune origin`. When this checkout is on the merged branch with a clean tree,
switch it to `main` and `git pull --ff-only`, after the stamp commit is on `main`, so the next
branch starts from the stamped changelog. A tree with changes in it is left as it is and reported.
Deleting local branches is `end-session`'s cleanup, unless the `Guide` says otherwise. Where the
`Guide` routes source control through another tool, use its forms of these commands.

## 11. Several PRs

Land them one at a time, in the order the user gave, each through steps 2 to 10. Each merge leaves
the others behind, and each catch-up waits for the previous merge's stamp (step 3). The first stop
ends the queue: report what merged and what is left.

## 12. Report

For each PR:

- the merge commit, the version tagged on it and the stamp commit, or why there is no tag yet;
- the release run and each deploy, with their outcomes;
- the issues it closed and their board status, and any mentioned issue that changed;
- what `Hook: after merge` did;
- anything held, and why: a red, a block, an unexpected closing reference, a question.

Then any follow-up PR opened and waiting for approval, and the settings files written in step 0.
