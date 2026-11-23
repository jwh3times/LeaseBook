---
# GENERATED — do not edit. Source: .agents/skills/dependabot/SKILL.md — regenerate with 'node scripts/sync-agents.mjs'.
name: dependabot
description: Review a repository's open Dependabot pull requests — triage each bump, read its CI first, reproduce the lockfile install, read the release notes of every major it crosses, and reach an evidence-based verdict — then, on the user's word, post the verdicts, push a known fix, and hand the PRs the user names to land. Use when the user says "/dependabot", "review the Dependabot PRs" or "what can merge", and from the scheduled Dependabot review. Never merges on its own.
metadata:
  settings: dependabot merging issue-tracker
---

# Dependabot

Turn a repository's open Dependabot pull requests into verdicts the user can act on: which are
routine, which bumps were investigated and are safe for this repository, and which need work, each
with its evidence. A confident but wrong verdict, in either direction, is worse than none: a false
"needs effort" makes work, and a false "safe to merge" sends a broken PR toward `main`. A major
version is a reason to investigate, never by itself a reason to warn.

**Announce at start:** "I'm using the dependabot skill to review the open Dependabot PRs."

Run shell snippets in bash (Git Bash on Windows). `.agents/skills/dependabot/dependabot.mjs` reads
the PRs, their checks, commits and comments over GitHub's REST API, and prints one JSON object; it
never writes. `.agents/skills/dependabot/resolution.md` says how to read each ecosystem's install;
read it before step 4. Merges go through `land`, installed beside this skill.

This skill never merges, approves, closes, labels or edits a pull request, and never posts an
`@dependabot` command; when one is needed, it gives the user the exact comment to post.

## Two ways to run

- **In a session**, the default: steps 0 to 6, then report (step 10). Posting the verdicts (step
  7), pushing a fix (step 8) and landing (step 9) each wait for the user's word, because each is a
  public write.
- **As the scheduled review**, when a routine's prompt runs this skill and says to post: steps 0 to
  7 for the repository, posting each verdict, then the report. Never steps 8 or 9, and never write
  a settings file: for a missing setting, take its default, or its `Discover:` value when it has
  none (`Public repository`), and report each setting that was missing.

## 0. Settings

This skill reads `docs/agents/dependabot.md`, `docs/agents/merging.md` and
`docs/agents/issue-tracker.md`. Follow `.agents/skills/dependabot/settings/README.md` to read them
and complete any that are missing; the definitions sit beside it. Resolve each `discover:` value
once, here, and reuse it below.

Then read the prose of `docs/agents/dependabot.md`, and the parts of the merging `Guide` about
dependency pull requests. Every fact used below comes from this repository's files: never carry a
fact from another repository's review into this one.

## 1. List

```bash
node .agents/skills/dependabot/dependabot.mjs list --repo "<Public repository>" --label "<Label>"
```

No pull requests: report that and stop. Otherwise work through `prs` oldest first, as listed. Leave
out, and report, a PR that is a `draft`, carries a label containing `hold`, `do-not-merge`,
`do not merge` or `wip`, or has a comment in `comments` from a person asking to wait.

## 2. Triage

- **Already reviewed:** a PR whose `verdict` is `current` is not reviewed again unless the user
  asks. A `low-risk` verdict stops being current when it no longer names the PR's versions
  (Dependabot retargeted it) or when the PR's checks have failed since; report it as stale, never
  as a basis for landing, and review the PR again: the new verdict, the latest marked comment,
  replaces it.
- **ROUTINE:** `kind` is `minor`, `patch`, `digest` or `removed`, and no update matches the `Review
  triggers`. It needs no release-note research, but step 3 still applies.
- **CANDIDATE:** `kind` is `major`, `unknown`, `downgrade` or `suffix`, or an update matches the
  `Review triggers`. A grouped PR is one candidate covering every member. With `parsed: false` (a
  person replaced the body), read the versions from the manifests' diff
  (`gh api "repos/<owner>/<repo>/pulls/<n>/files"`), and treat it as a candidate.

A candidate is something to investigate in step 5, not yet a verdict.

## 3. CI first, for every PR

Evidence outranks reasoning: read `checks` before any analysis.

- **`pending`:** wait for it, `list --pr <n> --wait 300`, for up to about ten minutes; in a session,
  go on with the others and come back. Still pending: no verdict this run.
- **`failed`:** read each check in `checks.failed`: `gh run view <runId> --log-failed`; while the
  run is still going, the job's log (`gh api repos/<owner>/<repo>/actions/jobs/<jobId>/logs`); and
  when neither can be read, its annotations
  (`gh api repos/<owner>/<repo>/check-runs/<checkRunId>/annotations`). Find the first real error.
  A red check is dispositive: the verdict on a red PR is NEEDS EFFORT, quoting that error, however
  clean the analysis looks. If the analysis says safe and CI says red, the analysis is wrong: find
  out why. A red whose log cannot be read at all still gets NEEDS EFFORT, naming and linking the
  check.
- **Red on `main` too:** before blaming the bump, look at the same check on `main`'s newest commit
  and on the other open Dependabot PRs. When they fail with the same error, it is `main`'s red, not
  this PR's: report it once for the repository and give no verdict on that check.
- **`cancelled`** checks are not red by themselves (a guard that cancels itself on Dependabot PRs,
  a superseded run); list them, and never give NEEDS EFFORT on cancelled checks alone.
- **`passed`** on a ROUTINE PR: it is done, with no verdict and no comment.

## 4. Reproduce the resolution

For each candidate, and each routine PR whose red is an install error. Read `resolution.md` first.
Check the PR's head out where it disturbs nothing: in a session, a separate worktree
(`git fetch origin "$head_ref" && git worktree add --detach "$scratch_dir" FETCH_HEAD`), removed
after; in a scheduled run, the run's own clone.

Run the `Resolution` commands for each manifest the PR changed (`gh api
"repos/<owner>/<repo>/pulls/<n>/files" --jq '.[].filename'`), under the toolchain they name, and
read the result as `resolution.md` says. A failed install is a real, merge-blocking conflict: NEEDS
EFFORT, quoting it. When it succeeds, run the gates the `Playbook` names for that dependency and
record what ran and what it returned. An action or image bump has no lockfile: CI is its build
gate. A toolchain this machine cannot provide is said so, and CI's result decides.

## 5. Investigate each candidate

For each flagged dependency, from its old version to its new:

1. **Read what changed:** the release notes, changelog or migration guide on the web for every
   major crossed (4 to 6 means 5 and 6). Pull out the concrete breaks: removed or renamed inputs,
   APIs and config, changed defaults, runtime requirements, behavior changes.
2. **Read how this repository uses it:** search, starting with `Where to look`: the `with:`
   inputs on an action's step, the imported APIs, the config files, the image tags and mounts.
3. **Decide where they meet:** does a break from 1 reach a use from 2? Name the file, the line or
   the step. Apply the `Grounding facts`, and only this repository's.

## 6. The verdict

- **VERIFIED LOW-RISK**, the common outcome for a major of an action or a library: only with CI
  green (step 3), and the install and the relevant gates reproduced and passed (step 4), or, for an
  action or an image, CI green and named as the gate. Two to four lines: what was reviewed and
  that it is safe to merge here, the gates that ran, and one sentence of evidence, what the major
  changed and why it does not touch this repository.
- **NEEDS EFFORT**, when a check is red, the install or a gate fails, step 5 found a specific,
  cited break that reaches this repository, or the `Grounding facts` name the bump as one that
  always breaks here: (1) the break or failure and the version it landed in, quoting the real
  error; (2) where it hits, the file, import, step or peer range; (3) the next steps, naming the
  `Playbook` fix when there is one, and whether the bump can be taken at all yet.

Rules, each from a verdict that once went wrong:

- Never safe to merge with a check red. Never safe from "the types compile" or "the removed API is
  not imported" without the install reproduced.
- Never cite an optional peer, or a range not read from the lockfile or the manifest.
- No NEEDS EFFORT on speculation: without a specific break that reaches this repository, and no
  `Grounding facts` case, the verdict is VERIFIED LOW-RISK.
- No "may", "might", "could", "possibly" or "risks" except when quoting a confirmed change. Every
  claim traces to release notes read (name the version), a command run (quote it) or a file read
  (name it). Never tell the reader to go and check something: the comment reports what was checked.

Write each verdict to its own file, named for the repository and the PR. It starts with `✅
**VERIFIED LOW-RISK**` or `⚠️ **NEEDS EFFORT**`, and its last line is `marker`, exactly.

## 7. Post the verdicts

In a scheduled run, and in a session once the user says to. Post only on a PR with no `verdict`,
or one whose verdict is not `current`:

```bash
gh api --method POST "repos/<owner>/<repo>/issues/<n>/comments" -F body=@"$verdict_file" --jq .html_url
```

Report each comment's URL. A post that returns no URL is reported as failed, never as posted.

## 8. Push a known fix (in a session, on the user's word)

For a NEEDS EFFORT PR whose cause is mechanical (the `Playbook`'s fix for it, or a generated file
to regenerate), offer the fix with the command and what it changes. On the user's word:

1. In a worktree on the PR's head (`git fetch origin "$head_ref" && git worktree add --detach
   "$scratch_dir" FETCH_HEAD`), run the fix and the gates it should turn green.
2. Show the diff. A change beyond the mechanical one (code edited to fit the new version) is the
   user's to review before it is pushed.
3. Commit it in this repository's commit style, push to the PR's branch
   (`git push origin "HEAD:$head_ref"`), and remove the worktree. Where the `Guide` routes source
   control through another tool, use its forms of these commands.

From then on Dependabot no longer rebases that PR (`maintainedByDependabot` false): catch it up by
updating the branch, never `@dependabot rebase`, which would drop the fix. Wait for its checks
(step 3) before offering it to land.

## 9. Land (in a session, on the user's word)

A PR is ready to offer when its checks passed on its current head and it is ROUTINE, or carries a
`current` `low-risk` verdict. List the ready ones, oldest first. The user's words name
the PRs to land; "land the ready ones" names that list as shown. Hand them to `land` one at a time,
in that order, each through all of its steps. After each merge, run step 1 again: Dependabot
rebases the others, or closes one and opens a replacement, which is a new PR the user has not
named.

## 10. Report

For each PR: its number and updates with their kinds, its CI state and the error behind a red, the
verdict (new or existing, with its URL once posted), and what to do next: land, push the named fix,
wait, or a decision for the user. Then: `main`'s own reds, fixes pushed, PRs landed (with `land`'s
report), PRs left out in step 1 and why, comments the user must post (`@dependabot recreate` for a
PR whose branch others pushed to and that has gone red, as exact text), and the settings files
written or found missing in step 0.
