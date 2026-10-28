<!-- Generated from settings/ship.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: ship

`docs/agents/ship.md` — what `ship` checks before it pushes, and how its PR links to an issue. Each
setting below is a `##` heading in that file.

## Fast checks

- **Read by:** ship
- **Value:** bulleted list, one per check: the folder it runs from, the command, and optionally
  `only when <paths> changed` and the command that fixes a failure (e.g. `web/`: `npm run lint`;
  repo root: `npm run sync:agents:check`, fixed by `npm run sync:agents`, committing both sides).
  The list holds the static gates of every required status check — formatters, linters, type
  checks, generated-config checks, the repository's own lint scripts — and each unit suite that
  finishes in about two minutes, run `only when` the paths it covers changed. Suites that need more
  (a service, a browser, a test matrix, a longer run) stay in CI: name them after the list as
  CI-only. So a push that would fail a fast gate is refused here instead of on the PR. Entries run
  in order: formatters before the generated-config check, which mirrors formatted sources. The
  changelog's own check is `Changelog check` in `releases.md`, not an entry here.
- **Discover:** the branch's required checks (`gh api repos/<owner>/<name>/rules/branches/main`
  for rulesets, else `gh api repos/<owner>/<name>/branches/main/protection --jq
  '.required_status_checks.contexts'`), each traced to the workflow job that reports it and the
  commands that job runs; then the formatters, linters and builds in the manifests and CI
  workflows. Time each unit suite once to place it. Name any required check that cannot run
  locally when proposing the list.

## Issue link

- **Read by:** ship
- **Value:** `none` — the PR body carries no issue link line, and `ship` writes it as before. Or:
  - **Branch pattern** — how a branch names its issue, e.g. `<type>/<issue#>-<slug>`, with the
    types in use.
  - **Link line** — the PR body's first line: `Closes #N` when the PR finishes issue `N`, and the
    line for one slice of a spec that stays open (e.g. `Part of #N`).
  - **Waiver line** — what opens the body when the user waived the flow, e.g.
    `Flow waived by the user: <reason>`.
  - **Exempt** — bulleted kinds of work whose branch carries no number and whose body has no link
    line (e.g. dependency bumps, docs-and-agent-config-only changes, release prep).
  - **Defined in** — the repository document that states the convention, when there is one.
- **Discover:** `AGENTS.md`, `CONTRIBUTING.md`, `.github/PULL_REQUEST_TEMPLATE.md` and the docs
  they point to for a branch-name or issue-link rule; recent merged PRs' branch names and first
  body lines. Propose `none` when no rule exists.
