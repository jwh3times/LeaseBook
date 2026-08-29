---
name: end-session
description: Use at the end of a work session or day, or when the user says "end session", "wrap up", "done for the day", or asks to "clean up the local workspace, update any private/ docs and/or github issues that need it from this session." Sweeps the session for durable discoveries and lands them in memory, GitHub issues, and private/ docs, then cleans the local workspace. LeaseBook-specific.
---

# End session

The one-line contract this skill exists to execute:

> clean up the local workspace, update any private/ docs and/or github issues that need it from this
> session.

Ending a session cleanly means nothing learned today survives only in the transcript. Four
destinations, in this order — memory, GitHub issues, `private/` docs, then the workspace itself.

**Announce at start:** "I'm using the end-session skill to wrap up this session."

## Why this exists

This repo's durable state is split across four places with different visibility, and a session's
discoveries land in different ones:

- **Memory** (`~/.claude/projects/C--Users-jerry-OneDrive-Documents-VSCodeProjects-LeaseBook/memory/`)
  — cross-session orientation. Local to the machine, never committed.
- **GitHub issues** — the issue tracker per [`docs/agents/issue-tracker.md`](../../../docs/agents/issue-tracker.md).
  **This repo is public.** Anything written here is published.
- **`private/`** — confidential, ignored by the public repository, and **a separate versioned
  checkout with its own remote**. Not local-only: it is committed and pushed independently, per
  `private/README.md` § Two-repository workflow. `private/TODO.md` and `private/planning/*_retro.md`
  are the live source of truth for progress. The **public** CI can never see it; the private
  repository runs its own documentation gate (`private/scripts/check-docs.mjs`,
  `.github/workflows/docs.yml`), which checks structure, not whether the content is current — that
  is still this ritual's job.
- **The working tree** — build output, scratch files, stray branches, running containers. **Two
  repositories to sweep**, not one.

Public CI enforces none of this. `/ship` covers the public docs and changelog for a _branch_;
this skill covers the _session_, including everything `/ship` is forbidden to touch (`private/`) and
everything that never reaches a commit (memory, issues, workspace).

Run this **after** `/ship`, not instead of it. If a branch is ready for review, ship it first; this
skill does not open PRs.

## Steps

### 1. Reconstruct the session

Before writing anything, establish what actually happened. Do not rely on recollection alone —
check **both** trees, since a session usually moves both:

```
git status --short --branch
git branch --show-current
git log --oneline -15
git log --oneline origin/main..HEAD

git -C private status --short --branch
git -C private log --oneline -10
git -C private log --oneline origin/main..HEAD
```

Then list, for yourself, the session's candidate outputs in four buckets:

- **Discoveries** — a trap, a constraint, a corrected assumption, a thing that cost time and would
  cost it again.
- **Decisions** — something chosen or deferred that is not derivable from the diff.
- **Work state** — what shipped, what is half-done, what is blocked and on whom.
- **Debris** — files, containers, and branches created along the way.

If a bucket is genuinely empty, say so in the report rather than inventing an entry. A session that
only read code may legitimately produce no memory write and no issue edit.

### 2. Memory sweep

Path:
`C:\Users\jerry\.claude\projects\C--Users-jerry-OneDrive-Documents-VSCodeProjects-LeaseBook\memory\`.
One fact per file, `MEMORY.md` is the index (one line per memory, never content).

**Update before you create.** Read `MEMORY.md` first and match each discovery against the existing
files — most session findings belong in one that already exists:

| The discovery is about…                                           | Likely home                        |
| ----------------------------------------------------------------- | ---------------------------------- |
| Where the build stands, what shipped, what the next candidate is  | `milestone-state.md`               |
| A build/test/tooling trap that cost time                          | `leasebook-engineering-gotchas.md` |
| Seed, golden dataset, or demo-org constraints                     | `golden-dataset-audit-2026-07.md`  |
| Merge gating, required checks, branch protection                  | `main-branch-protection.md`        |
| Architecture-debt tickets and their grilling outcomes             | `architecture-review-2026-08.md`   |
| How the user wants work done (a correction, a confirmed approach) | a `feedback` memory                |

Rules that bite here:

- **Position, not history.** If `git log`, `private/roadmap.md`, or `AGENTS.md` already says it,
  do not restate it in memory. Record what none of them say.
- **Absolute dates**, never "yesterday" or "last week".
- Convert a superseded fact by **editing** the memory, not by appending a contradiction. Delete a
  memory that turned out to be wrong.
- Link related memories with `[[slug]]`.
- New file ⇒ add its one-line pointer to `MEMORY.md` in the same pass.
- **Security findings stay out of memory bodies in exploitable detail** — memory can point at
  `private/security-review-findings.md`; it should not restate an unpatched weakness.

### 3. GitHub issues

Conventions and exact `gh` invocations: [`docs/agents/issue-tracker.md`](../../../docs/agents/issue-tracker.md).
Label vocabulary: [`docs/agents/triage-labels.md`](../../../docs/agents/triage-labels.md)
(`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`).

Start from what is open and what this session touched:

```
gh issue list --state open --json number,title,labels --jq '[.[] | {number, title, labels: [.labels[].name]}]'
gh pr list --state open --json number,title,headRefName
```

Then reconcile:

- **An issue this session resolved** and whose PR has merged → comment the outcome and close it
  (`gh issue close <n> --comment "..."`). If the PR is open but not merged, comment the state; do
  not close.
- **An issue whose premise this session disproved or narrowed** → comment the correction. This
  happens often enough here to be the default expectation: tickets on the Wayfinder map are
  _questions_, and several have been materially wrong as filed. A grilled-and-narrowed ticket gets
  its narrowed scope written down, not left in the transcript.
- **Work discovered but not done** → file it. A discovery that only exists in memory is not
  scheduled work.
- **Labels** → move anything now fully specified to `ready-for-agent`; move anything blocked on the
  user to `ready-for-human` or `needs-info`.
- **Wayfinder map #196** — if the session grilled, resolved, or spun off one of its children, follow
  the wayfinding operations in the issue-tracker doc: comment the answer, close the child, and append
  a pointer to the map's Decisions-so-far. Sub-issue and blocked-by edges are `gh api` calls, not
  body text, where they are enabled.

**Never publish to an issue:** anything from `private/` — pricing, strategy, customer identity,
internal analysis, private figures — or exploitable detail about an **unpatched** security finding.
The repo is public. Those go to `private/security-review-findings.md` instead.

### 4. `private/` docs

A separate versioned checkout, ignored by the public repository. **Skip this whole step if
`private/` is absent** (public clone) — do not warn about the missing tree. `private/README.md`
indexes every document there and is the authority on which one owns what; the list below is the
subset a session close-out usually touches.

Match the update to the kind of thing learned:

- **`private/TODO.md`** — the master build plan, canonical for **what is done**. Tick the boxes this
  session completed. A scope change is an _edit to the plan_, not a note elsewhere. `GATE` items
  block everything below them; if the session cleared one, say so here.
- **`private/roadmap.md`** — the single M8 engineering document and canonical for **what the work
  is**: Track A/B/C in §3/§4/§5, exit criteria §10, open operator decisions §11. Its §12 ("Keeping
  this document honest") requires a completed item to tick its own checkbox **and** update the §1
  evidence and §2 status lines in the same change. Check §1's verified-open-positions list, §2's
  remaining table and its counts, and §3's execution-order markers. `/ship` only _warns_ about this
  drift; here you fix it. (The former `planning/m8_plan.md` overlay was folded into this file and
  deleted on 2026-08-28 — do not recreate a second M8 document.)
- **`private/planning/m{N}_plan.md` / `m{N}_retro.md`** — milestone overlays for M0–M7. There is no
  `m8_retro.md` yet. Deviations from the plan, known limitations, and what the next milestone must
  absorb belong in the retro, written at milestone close, not invented mid-milestone.
- **`private/security-review-findings.md`** — the only home for weakness and exploit detail, patched
  or not.
- **`private/architecture-review-findings.md`** — architecture-debt findings and their disposition,
  including "shipped a different fix than the finding asked for", which is the common case.
- **`private/planning/design-history/`** — historical design specs and implementation plans
  (renamed from `superpowers/` on 2026-08-28). Archive: read it, never update it. Its links rot by
  design.

Then commit the private repository, and verify the boundary held:

```
node private/scripts/check-docs.mjs     # from the repo root, or scripts/check-docs.mjs from private/
git ls-files private                    # MUST return nothing
```

Three hard rules:

- **Never stage a `private/` path into the _public_ repository.** `git ls-files private` must return
  nothing; a `git add -f` there would publish confidential material. This is the rule that matters,
  and the private gate asserts it too.
- **Do commit and push inside the private checkout**, as its own repository — that is the documented
  workflow in `private/README.md`. Public first, then record the public PR number or SHA in the
  private plan, then commit the private update. The two histories stay independent.
- **Never copy `private/` content into a committed public file, a public doc, a PR body, or a GitHub
  issue.**

### 5. Clean the local workspace

Work outward from the tree. **List before you delete, and confirm anything that is not obviously
regenerable build output.**

Uncommitted work first — this is the one that loses real work, and there are **two** repositories to
check:

```
git status --short
git stash list
git -C private status --short
git -C private stash list
```

For each untracked or modified file, decide out loud: commit it, stash it, or delete it. **Do not
delete or discard uncommitted changes without the user's explicit yes.** If a change belongs on a
branch that is ready, that is `/ship`, not this skill. Uncommitted work in `private/` is committed
there directly (step 4) — it never waits on a PR.

Regenerable output safe to sweep once identified (all gitignored; confirm the list first):

- `TestResults/` at the repo root, and `tests/**/TestResults/`
- `web/playwright-report/`, `web/test-results/`, `web/e2e-results/`
- stray `*.tsbuildinfo`, `*.trx`, `*.coverage`
- scratch files written outside the session scratchpad — anything ad hoc left at the repo root

Then the rest of the local environment:

- **Generated-mirror drift.** If the session edited `.claude/agents/` or `.agents/skills/`, the
  mirrors must be regenerated and committed or CI fails:

  ```
  node scripts/sync-agent-mirrors.mjs --check
  ```

  If stale, run it without `--check`, then `npm run format` from `web/` **before** re-syncing —
  formatting the generated copy just re-drifts it.

- **Containers.** Stop what the session started; both keep their data volumes:

  ```
  ./scripts/dev.ps1 down       # Postgres-only inner loop
  ./scripts/dev.ps1 app-down   # full Docker stack
  ```

  Leave them running only if the user says they are coming back to them. `reset-db` is destructive
  and is **not** part of cleanup — never run it here.

- **Branches and worktrees.** Report unpushed commits in **both** repositories
  (`git log --oneline origin/main..HEAD`, and the same with `-C private`) and list merged local
  branches, but **do not delete a branch without confirmation**:

  ```
  git worktree list
  git branch --merged main
  git cherry origin/main origin/<branch>   # before deleting any remote branch
  ```

  **`git branch --merged` does not prove a branch is drained.** This repo squash-merges, so a
  merged branch's commits were never ancestors of `main` and every one of them looks unmerged.
  `git cherry` is the real check: `-` is merged-equivalent, `+` is genuinely unlanded. A `+` line
  usually means a commit was pushed to the branch _after_ its PR merged, which nothing warns about.

- **Scratchpad.** Session scratch files live in the OS temp scratchpad, not the repo. Leave them;
  they are already outside the working tree.

### 6. Report

Close with a short, honest account:

- Memories written, updated, or deleted — by slug.
- Issues commented, closed, labelled, or filed — by number, with URLs.
- `private/` files updated, whether the private repository was committed and pushed, and the
  confirmation that `git ls-files private` from the public root still returns nothing.
- Workspace: what was deleted, what was left alone and why, container and branch state, and any
  uncommitted work still sitting in **either** tree.
- **Anything deliberately not recorded** — an unresolved question, a finding too raw to file. Say it
  plainly so it does not silently evaporate at the end of the session.

If a branch is still unshipped, say so and point at `/ship`; do not ship it as a side effect.

## Do not

- Open, update, or merge a PR — that is `/ship`.
- Stage a `private/` path into the **public** repository, or quote `private/` content anywhere
  public. (Committing **inside** the private checkout is expected — see step 4.)
- Publish private figures, strategy, or unpatched-security detail to a GitHub issue or any committed
  file.
- Delete uncommitted changes, stashes, or branches without explicit confirmation.
- Run `./scripts/dev.ps1 reset-db`, drop volumes, or otherwise destroy local data.
- Invent memory entries, issue comments, or retro content for a session that did not produce them.
- Restate in memory what `git log`, `AGENTS.md`, or `private/roadmap.md` already records.
