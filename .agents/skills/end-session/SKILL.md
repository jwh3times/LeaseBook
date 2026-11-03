---
name: end-session
description: Close out a work session — capture what was learned into memory, bring GitHub issues, the board, human follow-ups and docs up to date, and clean the local workspace. Use when the user says "end session", "wrap up" or "done for the day", or when the handoff skill reaches its end-session step.
metadata:
  settings: workspace issue-tracker human-follow-up docs triage-labels
---

# End session

Close out the session so the next one starts from an accurate picture instead of reconstructing
one. A session's durable output is more than its diff: what it **learned** (memory), what it
**moved** (issues and the board), what a **human** must now do (follow-ups), what its **docs** now
get wrong, and what it **left behind** (the workspace). Sweep those, in that order.

**Announce at start:** "I'm using the end-session skill to close out this session."

Run shell snippets in bash (Git Bash on Windows). Where `gh` is unavailable, use the equivalent
GitHub MCP operation: a missing CLI changes the interface, not the step.

This skill leaves shipping to the `ship` skill: no version bump, changelog section, source push,
or PR. A finished branch is shipped first, then this runs; a mid-flight branch stays mid-flight,
recorded where it stands.

Three rules hold throughout:

- **Evidence, not recollection.** An empty bucket is a valid outcome; say so and move on. An
  invented memory, comment, or entry is worse than none.
- **List before deleting.** Every deletion — file, branch, worktree, container — is shown as a list
  and waits for a yes.
- **Ask before committing or discarding**, except where a setting pre-authorizes it.

## 0. Settings

This skill reads `docs/agents/workspace.md`, `docs/agents/issue-tracker.md`,
`docs/agents/human-follow-up.md`, `docs/agents/docs.md` and `docs/agents/triage-labels.md`. Follow
`.agents/skills/end-session/settings/README.md` to read them and complete any that are missing; the
definitions sit beside it. Resolve each `discover:` value once, here, and reuse it below.

**Invoked by handoff:** when the arguments say `invoked by handoff; document at <path>`, the
handoff document is already written and registered. Carry its path into the report, and treat the
user's work as continuing on the other machine, not in this checkout.

## 1. Take stock

Establish what the session did before writing anything:

```bash
git status --short --branch
git branch --show-current
git log --oneline origin/main..HEAD
gh pr list --author @me --state open --json number,title,headRefName
```

Then run the `Workspace status command`. Sort what the session produced into the buckets below;
each routes to a later step.

| Bucket | Step |
| --- | --- |
| Durable facts about how to work in this repository or environment | 2. Memory |
| Work started, finished, blocked, discovered, or understood differently | 3. Issues |
| Actions only a human can now take | 4. Human follow-ups |
| Decisions, findings, and state changes the docs should hold | 5. Docs |
| Files, branches, worktrees, containers left behind | 6. Workspace |

## 2. Memory

Memory lives outside the repository, one fact per file, in the memory directory the harness names
in its instructions. Use that path exactly as given: a path derived from the checkout location
differs per machine and per tool, and writes to a wrong one succeed silently and are never read. If
the harness names no memory directory, skip this step and say so in the report. `MEMORY.md` there
is the index: one line per memory, never content.

Save a candidate only when **a fresh session here would get it wrong without the note** and the
repository does not already record it. `AGENTS.md`, the docs, the issue tracker, the changelog,
and Git history are durable; a memory restating them goes stale the moment they change. Memory is
for what is written nowhere: a trap that cost time, a preference the user stated, the state of
half-decided work.

- Read `MEMORY.md` first, and update a matching memory rather than adding a near-duplicate.
- Correct or delete a memory this session proved wrong.
- Write absolute dates, link related memories with `[[slug]]`, and keep every `MEMORY.md` pointer
  in step with the files.
- Keep an unpatched security weakness out of memory bodies; point at where it is tracked instead.

## 3. Issues and the board

Issues carry the work; the `Board` carries its state. Labels are the five in
`.agents/skills/end-session/settings/triage-labels.md`, plus any `Extra labels` in
`docs/agents/triage-labels.md`.

Board writes need the `gh` token's `project` scope, and fail silently without it. Check
`gh auth status` for `project` (not just `read:project`). If it is missing, report it and skip board
writes; the fix (`gh auth refresh -h github.com -s project`) needs a real terminal.

Start from what is open, in the `Public repository` and again in the `Private issue repository`
when there is one — always selected explicitly with `-R`:

```bash
gh issue list -R <repository> --state open --json number,title,labels \
  --jq '[.[] | {number, title, labels: [.labels[].name]}]'
```

For each issue the session touched:

- **Shipped** — comment the outcome and what changed about the understanding of the problem. Close
  it once its PR has merged; while the PR is open, leave it open and name the PR.
- **Advanced but unfinished** — comment where it stands and the next concrete step, written for a
  reader arriving cold.
- **Blocked** — comment the blocker, and set the `Board fields` that record it.
- **Understanding changed** — correct the body, or comment the correction. An issue describing the
  wrong problem costs more than a missing one.
- **Newly discovered work** — record it as `New work` says: an issue, labelled and added to the
  `Board`; or a `Board` draft item (`gh project item-create`), left for the work's commitment to
  turn into an issue. Set its board fields either way. Work that never reaches the board is
  invisible to the next session.

A security finding follows the `Security findings` route, never a public issue before its fix
ships. Keep private-repository links and content out of public issues, PRs, and commits. Leave
issues the session never touched to the `triage` skill.

## 4. Human follow-ups

Run the `## Procedure` in `.agents/skills/end-session/settings/human-follow-up.md` for every
required human action left by the session's completed work, including ones other skills
already started (docs-updater, `ship`): reconcile those rather than duplicating them.

## 5. Docs

Run the `docs-updater` skill on the session's work, in a subagent where the harness has one, told to
follow `.agents/skills/docs-updater/SKILL.md`; otherwise follow it yourself. A subagent cannot ask
the user, so complete its settings first, here, by following its step 0. Use `fix` mode for work not
yet merged, and `check` mode for work `ship` already refreshed and merged, recording any drift it
reports as new work, as step 3 does. Merged work whose `ship` report owed a wiki search gets one
more run in `fix` mode, limited to the wikis: those stale references are fixed, not filed.
Tell it this is `end-session`, and give it the session's discoveries as a list of changes beside
the diff: decisions and findings land in the `Doc map`
documents whose **when** they match, honoring each one's marker. Run step 4's procedure for any
human actions it returns. Its edits come back uncommitted, and join step 6's question about
uncommitted work. Keep its report for step 7.

- **Companion.** When the `Private companion` is set and its checkout exists, inspect it from its
  own root: the outer repository's `git log`, `git show` and `git diff` cannot see inside it. When
  the checkout is absent, report that companion docs could not be reconciled, and create no
  directory in its place. Commit companion changes by the `## Procedure` in
  `.agents/skills/end-session/settings/workspace.md`.
- **Changelog debt.** A behavior change committed this session without its `Unreleased` entry is
  debt to report; `ship` writes the changelog.
- **One tracker.** A backlog, roadmap, or TODO file the session added to the tree duplicates the
  issue tracker; offer to move its content into issues.

## 6. Workspace

**Uncommitted work first** — it is the one thing cleanup can lose for good:

```bash
git status --short
git stash list
```

Run the same in the companion when it exists. For each change, ask: commit it on its branch, stash
it with a named message, or leave it. Leaving it is a valid answer; say so in the report. A
companion change is committed only by the `## Procedure` in
`.agents/skills/end-session/settings/workspace.md`.

**Branches.** A branch is finished when its PR merged at or after the branch's local tip — true for
squash and merge commits alike, where `git branch --merged` misses every squashed branch:

```bash
git fetch --prune origin
for branch in $(git for-each-ref --format='%(refname:short)' refs/heads | grep -vx main); do
  tip=$(git rev-parse "$branch")
  pr=$(gh pr list --state all --head "$branch" --json number,state,headRefOid \
    --jq 'sort_by(.number) | last | select(.) | "\(.number) \(.state) \(.headRefOid)"')
  read -r number state head <<< "$pr"
  verdict=unfinished
  if [ "$state" = MERGED ]; then
    git cat-file -e "$head^{commit}" 2>/dev/null || git fetch -q origin "pull/$number/head"
    git merge-base --is-ancestor "$tip" "$head" && verdict=finished
  elif [ -z "$number" ] && git merge-base --is-ancestor "$tip" origin/main; then
    verdict=finished
  fi
  echo "$branch ${number:-no-pr} ${state:-none} $verdict"
done
git worktree list
```

Delete finished branches with `git branch -D` once the user agrees (`-d` refuses squashed ones). A
branch with an open PR, no PR and commits of its own, or commits after its merged head holds work:
keep it, and name `ship` in the report when it is ready. A remote branch still listed after the
prune gets the same check before `git push origin --delete`. Remove a finished worktree with
`git worktree remove <path>`, then `git worktree prune`.

**Return to `main`** last, after the tidying below, unless the tree has uncommitted tracked changes
or the user is continuing on this branch in this checkout. Record `before="$(git rev-parse HEAD)"`,
run `git switch main`, then `Sync with main`. If the sync refuses to fast-forward, local `main` has
commits of its own: report it and leave `main` as it is. A companion that has diverged from its
upstream (commits on both sides) is reported, never merged, rebased, reset or force-pushed. For
each dependency lockfile in `git diff --name-only "$before" HEAD`, install from that lockfile in its
folder, in the frozen form the repository's `AGENTS.md` gives (`npm ci`,
`pnpm install --frozen-lockfile`, `dotnet tool restore`), asking first where `AGENTS.md` gates
installs. Then run `Hook: after sync` from `docs/agents/workspace.md`.

**Files.** Delete `Safe-to-remove paths`, and stray scratch files the session left in the tree, by
explicit path after listing them. `Never-remove paths` stay unless the user explicitly asks;
blanket deletes such as `git clean -x` would take them too, so delete only by path.

**Then:**

- `Dev stack` — report what is running and ask before stopping it; a running database may be
  deliberate. Never run its **Never run** commands here.
- `Generated-config check` — when it reports drift, regenerate, and commit both sides.
- `Hook: end-session cleanup` from `docs/agents/workspace.md`.
- `Final portability check`, last. Name the exact state of each repository rather than calling the
  checkout synchronized.

## 7. Report

Run this last, so the counts are the session's final state. Set `public_repository` from
`Public repository` and `companion_repository` from `Private issue repository` (empty for `none`).
For each other repository the session wrote to (pushed to, or opened a PR or issue in), run it again
with that repository's own settings.

```bash
count_open() {
  if result="$(gh api --paginate "$endpoint" --jq "$field" 2>&1)"; then
    printf '%s' "$result" | grep -c .
  else
    case "$result" in
      *disabled*|*"not enabled"*|*"no analysis found"*|*"must be enabled"*) echo disabled ;;
      *) echo "not readable" ;;
    esac
  fi
}
for column in "public $public_repository" "companion $companion_repository"; do
  read -r role repository <<< "$column"
  [ -n "$repository" ] || continue
  echo "$role open PRs: $(gh pr list -R "$repository" --state open --limit 500 --json number --jq length 2>/dev/null || echo 'not readable')"
  field='.[].number'
  for kind in dependabot code-scanning secret-scanning; do
    endpoint="repos/$repository/$kind/alerts?state=open&per_page=100"
    echo "$role $kind: $(count_open)"
  done
  if [ "$role" = public ]; then
    endpoint="repos/$repository/security-advisories?state=draft&per_page=100"
    field='.[].ghsa_id'
    echo "$role draft advisories: $(count_open)"
  elif ! labels="$(gh label list -R "$repository" --limit 500 --json name --jq '.[].name' 2>/dev/null)"; then
    echo "$role security issues: not readable"
  elif ! printf '%s\n' "$labels" | grep -qx security; then
    echo "$role security issues: no label"
  else
    gh issue list -R "$repository" --state open --label security --limit 500 --json labels --jq "
      [.[] | [.labels[].name] | any(. == \"wontfix\" or . == \"deferred-trigger\")] |
      \"$role security issues: open \(map(select(. | not)) | length), accepted risk \(map(select(.)) | length)\"" \
      2>/dev/null || echo "$role security issues: not readable"
  fi
done
```

Then print this template, every section every time, with `none` where there is nothing to say:

```markdown
## End of session: <repository>, <date>

### What this session did
- Memory: <added, updated or deleted, each with its reason; or why this step was skipped>
- Issues: <commented, closed, labelled or opened, by number>
- Workspace: <deleted; kept and why; uncommitted changes, unpushed branches, running containers>
- Settings files written: <list from step 0>
- Handoff document: <its path, when handoff invoked this skill>
- Not done: <everything skipped or left unfinished, and changelog debt>

### Docs
- Searched: <documents checked, the terms searched for, and where, from docs-updater's report>
- Updated: <documents and their commit state, the companion's too, or that it was absent>
- Drift reported, not fixed: <list>

### Repository status
| | Public | Private companion |
|---|---|---|
| Open PRs: this session | n | n |
| Open PRs: others and bots | n | n |
| Dependabot alerts | n | n |
| Code scanning alerts | n | n |
| Secret scanning alerts | n | n |
| Draft advisories | n | n/a |
| `security` issues: open | n/a | n |
| `security` issues: accepted risk | n/a | n |

### Human steps before the next session
- <what, where, and why it blocks>

### Human follow-ups
- <verified issue or wiki link, or what is unpublished and why>
```

- **Status cells** hold a count, `n/a`, or what the snippet printed: `disabled`, `not readable` or
  `no label`. Those three are never written as `0`: none of them says nothing is open. `this session`
  counts the PRs this session opened that are still open, and `others and bots` is the rest of the
  total. An open `security` issue also labelled `wontfix` or `deferred-trigger` is an accepted risk.
- **Columns.** Leave out the companion column when there is no companion. Each other repository
  gets its own pair, headed by its public name; a companion whose repository cannot be resolved has
  `not readable` in every cell.
- **Human steps before the next session** holds only what stops the next session: a PR to merge, an
  expired login, work to push from this machine, a decision the user owes. Tracked work with no such
  urgency is a follow-up.
- **Counts and roles only.** The security rows carry no title, identifier or package name, and the
  report names a companion by its role, never its repository. A report holding a companion's counts
  stays out of public issues, PRs and commits.

Name plainly what was not done. An accurate "these three things are still open" is the point of the
skill.
