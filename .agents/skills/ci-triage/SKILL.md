---
name: ci-triage
description: Diagnose a red CI run, read-only — find the failed checks of a pull request, run or commit, read each failing step and its log and annotations, match the error against the repository's known reds, compare with the same check on main and on the PR's base, and classify each failure as known red, runner/infra, likely flake, real failure or unknown, with the evidence. Never re-runs, comments or pushes. Use when land or dependabot meets a red check, or when the user asks why CI failed.
argument-hint: "A PR number, a run id or URL, or a commit sha"
context: fork
model: inherit
background: false
metadata:
  settings: merging issue-tracker
---

# CI triage

Say why a check is red, so the caller can act on evidence: apply a known remedy, wait out a
runner problem, or fix the code. The logs are long and the answer is short; this skill reads the
logs and returns the answer.

**Announce at start:** "I'm using the ci-triage skill to read the red checks on <target>."

**Where the harness cannot fork.** This skill is written to run in a subagent of its own (Claude
Code reads `context: fork`, `model: inherit` and `background: false` above, and runs it on the
caller's model). A harness that loads it as an ordinary skill (Codex), or a caller that follows it
inline, runs every step below itself, unchanged. A subagent starts without the caller's
conversation, so the target comes in the arguments: a PR number, a run id or URL, or a sha, and
the check names when the caller knows them.

Run shell snippets in bash (Git Bash on Windows).

**Read-only.** Never re-run a job or workflow, push, comment, label, approve, edit or dispatch
anything, and never change a setting. A remedy is named for the caller, who applies it; a red is
never re-run "to see if it passes".

**REST first.** Every read below is a REST call (`gh api repos/...`, `gh run view`), which works
where GraphQL is refused (a cloud environment answers GraphQL with 403).
`.agents/skills/land/land.mjs pr` adds which checks are required, through GraphQL: use it when it
answers, and go on without it when it does not.

## 0. Settings

This skill reads `docs/agents/merging.md` and `docs/agents/issue-tracker.md`, for the `Guide` and
the `Public repository`. Follow `.agents/skills/ci-triage/settings/README.md` to read them; the
definitions sit beside it. Running as a subagent, no user can answer, so write nothing: a missing
`Guide` takes its default, a missing `Public repository` its **Discover** value from the `origin`
remote (`git remote get-url origin`), and the report names each setting that was missing.

Then read the `Guide` in full. Its account of known reds (each a signature and its remedy), checks
that never gate and jobs that are slow is the first thing every failure is matched against.

## 1. The failed checks

`repo` is the `Public repository`, `owner/name`.

- **A PR:** `gh api "repos/$repo/pulls/$pr" --jq '{head: .head.sha, base: .base.sha, baseRef: .base.ref, state, merged}'`.
  The head is the commit whose checks are read.
- **A run id or URL:** `gh api "repos/$repo/actions/runs/$run_id" --jq '{name, event, head_sha, head_branch, run_attempt, conclusion, workflow_id}'`.
  A run on attempt 2 or later was re-run: read the attempt the caller named, or each failed one, as
  `repos/$repo/actions/runs/$run_id/attempts/<n>/jobs`. Find its PR with
  `gh api "repos/$repo/commits/<head_sha>/pulls"`.
- **A commit:** the sha itself.

Then the failed checks on that commit:

```bash
gh api "repos/$repo/commits/$sha/check-runs?per_page=100" \
  --jq '.check_runs[] | select(.conclusion != null and .conclusion != "success" and .conclusion != "skipped" and .conclusion != "neutral") | {id, name, conclusion, app: .app.slug, url: .html_url}'
gh api "repos/$repo/commits/$sha/status" --jq '.statuses[] | select(.state != "success") | {context, state, description, target_url}'
```

A GitHub Actions check run's `id` is its job's id. A `cancelled` check with a passing check of the
same name on the same commit is a superseded duplicate, not a red. A check from another app (a
deploy, a code scanner) has no Actions log: read its `output` (`gh api "repos/$repo/check-runs/<id>"
--jq .output`) or its status's description and link, and classify from that.

## 2. The failing step first

For each failed Actions job, find its run (`gh api "repos/$repo/actions/runs?head_sha=$sha&per_page=100"`)
and read its steps:

```bash
gh api "repos/$repo/actions/runs/$run_id/jobs?per_page=100" \
  --jq '.jobs[] | select(.conclusion == "failure" or .conclusion == "timed_out" or .conclusion == "cancelled") | {id, name, conclusion, runner_name, steps: [.steps[] | select(.conclusion != "success" and .conclusion != "skipped") | {number, name, conclusion}]}'
```

The first step that failed says what kind of red it is before any log is read. A failure in set up
job, checkout, a cache or toolchain setup, a container pull or a dependency install, before any
build or test step ran, is **runner/infra** unless its log shows the repository's own files at
fault (a lockfile out of step with its manifest is real). A job with no failed step that was
cancelled or timed out is read from the run's own conclusion and timing.

## 3. The log and the annotations

```bash
gh api --allow-escape-sequences "repos/$repo/actions/jobs/$job_id/logs" > "$log_file"
gh api "repos/$repo/check-runs/$job_id/annotations" --jq '.[] | {path, start_line, annotation_level, message}'
```

Write the log to a file in a scratch location and search it; never print it whole. Strip colour
codes before searching (`sed 's/\x1b\[[0-9;]*m//g'`). Annotations often hold the runner's summary
of a test failure (the test, the assertion, the source line), and are read first when they do.
A log that cannot be read (expired, or the job still running) leaves the annotations and the step.

## 4. The error lines

From the failing step's section of the log (between its `##[group]` and the `##[error]` that ends
it), take:

- the first error, not the last: later errors are often its consequences;
- the summary line of a test runner (`Tests: 1 failed`, `failed: 666`, `1 failed`), and the names
  of the failing tests;
- whether the failures are assertions in tests, or setup and teardown around them (a fixture's
  `DisposeAsync`, a container's removal, a browser launch) — and when one cause repeats for every
  failing test, say so with the count.

## 5. Known reds

Match the error lines against every red the `Guide` names as not the code: its signature (a step,
a message, a check name) against what step 4 found. A match is **known red**, carrying the
`Guide`'s remedy word for word and whether the `Guide` allows that remedy once or more. A check
the `Guide` says never gates is reported as such.

## 6. Compare

- **The same commit, another attempt.** In the run's other attempts (`run_attempt` above 1, or an
  earlier one): the same job passing on the same sha is the strongest flake evidence there is.
- **`main`.** The same workflow's recent runs on `main`
  (`gh api "repos/$repo/actions/workflows/<workflow_id>/runs?branch=main&per_page=10"`) and the same
  job in each (`.../runs/<id>/jobs`): green before and after, red now, or red on `main` too. A red
  `main` shares with the same error is `main`'s red, not this change's.
- **Identical code.** For a PR, compare its head with the last `main` commit where the job passed:
  `gh api "repos/$repo/compare/<green main sha>...<head>" --jq '[.files[].filename]'`. When none of
  the files that differ is code the job builds or tests (only docs, changelog fragments, release
  stamps, agent config), the job failed on code that passes on `main`.

## 7. Classify

Each failed check gets one class, the first that fits:

1. **Known red** — the `Guide` names it (step 5). Give its remedy as the `Guide` words it.
2. **Runner/infra** — the failure is in the runner or a service around the code, not in the code:
   a step before any build or test ran (step 2), or every failure is in setup or teardown with a
   network, Docker, disk, rate-limit or cancellation error (a socket error removing a test
   container, `No space left on device`, `toomanyrequests`, a 5xx from a registry), with no
   assertion failing.
3. **Likely flake** — an assertion or timeout failed, and the same job passed on identical code:
   another attempt on the same sha, or `main` on code that differs only in files the job does not
   test (step 6). Name the test and the evidence; this is never a reason to re-run unless the
   `Guide` names a remedy for it.
4. **Real failure** — an error the change explains: a test asserting on behaviour the change
   touched, a compile or lint error in a changed file, a check `main` passes on code the change
   altered. Quote the error and point at the code: the file and line in the log, and the changed
   line it reaches.
5. **Unknown** — nothing above fits on the evidence read. Say what was read, what was not (an
   expired log), and what would decide it.

Never call a failure a flake on its shape alone: a timeout or a "not found" in a UI test is a real
failure until identical code is seen passing.

## 8. Report

Return this, and nothing longer; the logs stay here.

```markdown
## CI triage: <repo> <PR #n / run id / sha> (<head sha, short>)

| Check | Failed step | Class | Evidence |
| --- | --- | --- | --- |
| <workflow / job> | <step> | known red / runner/infra / likely flake / real failure / unknown | <one line> |

### <check>
- **Error:** `<the first error line, quoted>` (<file:line>, when the log names one)
- **Why this class:** <the step, the comparison, the Guide entry>
- **Remedy:** <the Guide's remedy, word for word; or "none: fix the code at <file:line>"; or
  "none named: report to the user">
```

Then `main`'s own reds, if any, checks that were cancelled as duplicates, and the settings that
were missing.
