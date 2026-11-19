# Merging

- **Audience:** Coding agents and maintainers
- **Status:** Living policy
- **Owner:** Maintainers
- **Last reviewed:** 2026-11-18

Everything from an open pull request to a finished merge. `/ship` stops when the PR opens, and the
maintainer merges by saying so. Each rule here comes from a merge that went wrong.

## What gates a merge

`main` is protected by one repository ruleset; classic branch protection was deleted, so
`.../branches/main/protection` answers 404. Read what applies with
`gh api repos/jwh3times/LeaseBook/rules/branches/main`.

- **No bypass, no approving review.** The maintainer is the only reviewer and can't approve their
  own PR, so the ruleset requires none, and nobody can bypass it. CI is the review.
- **Branches must be up to date, and auto-merge is off.** When a PR falls behind:
  `gh pr update-branch <n>`, watch CI on the new head, then merge by hand.
- **Only unconditional checks are required.** `Link check (lychee)` is path-filtered to Markdown and
  never runs on a code-only PR, so requiring it would block every such PR forever. Never add it, and
  never require a check whose job has a `paths:` filter or a job-level `if:`.
- **CodeQL gates through the ruleset's `code_scanning` rule**, though its check isn't required. A
  medium-or-higher alert leaves a PR `BLOCKED` with every check green. `CodeQL` sitting at `NEUTRAL`
  while its jobs run is normal, not a block forming. A false positive is cleared by dismissing the
  alert:
  `gh api repos/jwh3times/LeaseBook/code-scanning/alerts/<n> -X PATCH -f state=dismissed -f dismissed_reason="false positive" -f dismissed_comment="…"`.
  The comment is capped at 280 characters, and the reason is `false positive`, `won't fix` or
  `used in tests`.
- **A shared generic helper is a false-positive factory for taint queries**: CodeQL can enter it at
  one call site and leave at another's consumer. Pull the analysis's code flow
  (`gh api repos/jwh3times/LeaseBook/code-scanning/analyses/<id> -H "Accept: application/sarif+json"`)
  and find the step where the path leaves the helper.
- **Copilot review never posts and doesn't gate**, despite the ruleset rule. Don't wait for it.
- **To change the ruleset**, GET it whole, change one entry, and PUT back
  `{name,target,enforcement,conditions,rules}`: a partial payload drops the other rules. A
  `required_status_checks` entry is `{context}` only; adding `integration_id: null` fails the whole
  PUT with 422. The ruleset's id is in the private companion.

## Watching CI

- **Watch the PR's head commit, not the PR.** `gh pr checks --watch` can return the previous run's
  results at once. Check `gh api repos/jwh3times/LeaseBook/commits/<head sha>/check-runs`.
- **`gh run list --commit <sha>` needs the full 40-character sha.** An abbreviated one matches
  nothing, so a "wait until nothing is pending" loop exits at once. Guard on `total > 0` as well as
  `pending == 0`.
- **The number of checks isn't fixed.** A docs-touching PR runs the link check and a code-only one
  doesn't. Wait for "none pending, none failed, `mergeStateStatus` `CLEAN`", never a hard-coded
  total.
- **A wait loop must survive one failed call.** A single TLS timeout ended a `gh pr checks` loop and
  read as a failed check while CI was fine.
- **A check can report `in_progress` with every step green** after its run completed, a stale flag
  on GitHub's side. Read the job's steps before treating "pending" as running.
- **A workflow's own commit leaves the PR `BLOCKED`.** The commit `update-visual-baselines.yml`
  pushes starts CI runs that sit at `action_required`. Approve each:
  `gh api -X POST repos/jwh3times/LeaseBook/actions/runs/<id>/approve`, with ids from
  `gh run list --branch <b> --json databaseId,headSha,conclusion`.
- **Re-running a failed job replays the original event**, so a label added since is invisible to it.
  Push a commit, or close and reopen the PR, to fire a new event.
- **A re-run also replaces what the run reports.** `gh run list` and `.../actions/runs/<id>/jobs`
  show only the latest attempt, so a scan of past failures that reads a run's conclusion misses every
  failure a re-run turned green. Read each attempt with
  `gh api repos/jwh3times/LeaseBook/actions/runs/<id>/attempts/<n>/jobs`; the run's `run_attempt`
  (`attempt` in `gh run view --json`) says how many there are.
- **A re-run stuck at `queued` that cancel, force-cancel and rerun all refuse** is cleared by
  `gh pr close <n> && gh pr reopen <n>`, which re-runs every check on the same sha.
- **A step that fails with `Process completed with exit code 1` and no `::error` annotation** died
  inside its script, not at its check. Read the raw step log.
- **`gh run view <run> --log-failed` refuses while any job of the run is still going** ("logs will
  be available when it is complete"), even once the failed job has finished. Read that job's log at
  once with `gh api --allow-escape-sequences repos/jwh3times/LeaseBook/actions/jobs/<job id>/logs`;
  without the flag, `gh api` refuses the colored log.
- **An e2e step red with test after test failing `net::ERR_CONNECTION_REFUSED`** usually means a web
  server Playwright started died mid-run (#798: a V8 abort in the Vite dev server on Node 26,
  nodejs/node#66870), not that the product regressed. Every workflow Playwright run goes through
  `scripts/e2e-server-crash.mjs`, which then adds an `E2E web server crashed` annotation quoting the
  first sign; re-run the job. A server that hung rather than died leaves no annotation.

## Merging several PRs

- **Each merge leaves the others behind**, and two PRs that both insert a `CHANGELOG.md` entry under
  the same heading go `DIRTY`, which `gh pr update-branch` can't fix. Merge `origin/main` locally,
  keep both entries, push. A script that merges several PRs must stop on `DIRTY`, not loop.
- **After pushing a merge commit, `headRefOid` can stay on the old sha** with no CI started for
  minutes. An empty commit re-syncs it.
- **The changelog gate accepts only the next `x.y.0`**, so parallel minors merge in order and the
  second is re-cut.
- **An approval covers the PRs it names.** "Merge the PRs" for two PRs doesn't extend to ones opened
  afterwards; ask for each batch.

## The merge itself

`gh pr merge <n> --merge --delete-branch` once every check on the current head is green.

- **It can merge and then fail during local cleanup**, when `main` is checked out in another
  worktree. Before retrying after that error, check
  `gh pr view <n> --json state,mergedAt,mergeCommit`.
- **Read `closingIssuesReferences` before merging a `Part of` PR** (`gh api graphql`); it must be
  empty. A closing keyword anywhere in the body closes the issue, even under `Part of`: "Close #678
  by hand after that" closed #678. Never put close, fix or resolve beside `#N` in prose unless you
  mean it.

## After the merge

"Merge and complete all follow-ups" is one unit of work: verify every required check is green,
merge, then without asking between steps:

- move the board item to Done and strip stale `ready-for-agent` labels;
- delete the local branch, and the remote one if it survived the merge (the repository's
  `delete_branch_on_merge` setting normally removes it);
- close a private issue by hand;
- file any issue the work spun off.

Report the merge commit and what the follow-ups touched. Two cases still need a question: an
ambiguous referent ("the PR" when two are open), and a check that isn't actually green.

- **A commit pushed after its PR merged is orphaned silently.** GitHub accepts it and nothing warns.
  Before deleting a stale branch, compare its tip tree with `main`'s
  (`git diff --quiet origin/main origin/<branch> --`). Neither `git branch -r --merged` nor
  `git cherry` is enough after a squash merge.

## Dependency pull requests

- **A green check proves the bump only against the base it was opened on.** Update the branch
  before signing one off: `cspell` and `oxlint` are CI gates, so a new dictionary or rule can fail on
  content merged since.
- **A red check doesn't prove the dependency broke.** Three times (Playwright, QuestPDF, msw 3) the
  fault was ours. Before pinning, ignoring or filing upstream:
  1. prove the product is unaffected under the new version, by checking the user-visible artifact;
  2. cross the versions (library old/new × sub-dependency old/new) to isolate the layer;
  3. read the upstream source diff, not only the release notes;
  4. if the change is intentional upstream, fix our side and don't pin.

  A bump that turns a guard on is telling you the guard was needed: read what each flipped default
  now enforces. A test that fails after a bump but passes alone usually ended with a request still
  in flight.

- **Find the failing test from its `failed` line and the run summary**, never from a stack trace in
  the log: a test that throws on purpose logs one too.
- **Stripe.net is pinned**, so a bump that moves its API version goes red by design. Follow "Raise
  the pinned API version" in [`docs/runbooks/stripe-sandbox-probe.md`](../runbooks/stripe-sandbox-probe.md).
- **Read a Docker bump's diff for a tag change**; its title shows only digests.
- **An ignore rule is proven when Dependabot closes its own PRs** for it, seconds after the config
  merges; check `gh api repos/jwh3times/LeaseBook/issues/<n>/events` before closing bot PRs
  yourself. A PR mixing an ignored and an allowed change stays open and needs a hand commit.
- **A Dependabot config change can be checked without the UI**: merging it starts a run per
  ecosystem within seconds, and `gh run list --workflow "Dependabot Updates"` shows each one's
  conclusion. A green run proves the config was accepted, not that a rule fired.
- **Dependabot's merges get their changelog sections on the next ship** (release prep,
  [dev-flow.md](dev-flow.md#exemption-details)).
