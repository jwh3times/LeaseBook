# Development Flow

- **Audience:** Coding agents and maintainers configuring engineering skills
- **Status:** Living policy
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-26

The engineering skills in `.agents/skills/` are this repository's binding development process.
`AGENTS.md` states the gate; this document owns its detail: what counts as a ready issue and as code,
where to route the user, the exemptions, and how a branch and PR link to their issue. The full skill
map — every flow, on-ramp and phase-boundary choice — lives in the `/ask-matt` router
(`.agents/skills/ask-matt/SKILL.md`); this document covers only what the gate adds to it.

## The gate

Every code change starts from a **ready issue**, one of:

- an issue labelled `ready-for-agent` that carries a `/to-spec` spec or a `/to-tickets` ticket in its
  body, or a `/triage` agent brief — `/triage` posts the brief as a comment, so read the comments too
  (`gh issue view <n> --comments`);
- a bug issue worked through `/diagnosing-bugs`. The issue number is required; a spec is not.

The gate fires at the first edit to code. Reading code, running tests and suites, and answering
questions need no issue. Check readiness before that first edit, and with no ready issue stop and
route the user (below). A ready issue on the private tracker counts; its number stays out of the
branch and PR (see [Linking work to its issue](#linking-work-to-its-issue)).

## What counts as code

Docs and agent config — exempt from the gate:

- any `*.md` file, including everything under `docs/`
- agent config: `.claude/`, `.agents/`, `.codex/`, `skills-lock.json`
- docs tooling config: `cspell.json`, `.markdownlint-cli2.jsonc`

Code — gated — is everything else: `src/`, `tests/`, `web/`, `scripts/`, `infra/`, `seed/`,
`.github/` workflows and templates, `.config/`, and the root build and runtime files (`Dockerfile`,
`docker-compose.yml`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`,
`LeaseBook.slnx`, `package.json`, `VERSION`). A change that touches both is code.

## Routing

With no ready issue, name the command that fits, say in one line why, and wait for the user to run
it:

| Situation                                               | Route                                                                                               |
| ------------------------------------------------------- | --------------------------------------------------------------------------------------------------- |
| An idea one session can hold                            | `/grill-with-docs`, then `/to-spec` — plus `/to-tickets` when the build spans sessions              |
| A huge, foggy, multi-session effort                     | `/wayfinder`, then `/to-spec` once the map clears                                                   |
| A raw issue someone else filed (`needs-triage` or none) | `/triage`                                                                                           |
| A ready issue nobody has started                        | `/implement`, in a fresh context                                                                    |
| Something broken                                        | File the bug issue (`bug` label, per [issue-tracker.md](issue-tracker.md)), then `/diagnosing-bugs` |

Tickets that `/to-spec` and `/to-tickets` publish are already `ready-for-agent`; they go to
`/implement`, never to `/triage`.

## Phases

The flow skills — `/wayfinder`, `/grill-with-docs`, `/to-spec`, `/to-tickets`, `/implement` and
`/triage` — carry `disable-model-invocation: true` and keep it: the human starts every phase. At a
phase boundary, name the next command, give the one-line reason, and wait; `/ask-matt` owns the
continue/`/clear`/`/compact` choice at that boundary.

Inside a phase, start the model-invocable skills whenever they fit: `/tdd`, `/code-review`,
`/diagnosing-bugs`, `/domain-modeling`, `/codebase-design`, `/prototype` and `/research`.

`/implement` commits to the current branch, and `main` is protected. Cut the topic branch before the
first commit.

## Exemptions

These changes need no ready issue:

- **Dependency bumps** — Dependabot PRs and hand-made bumps titled `build(deps):`, this repository's
  Dependabot prefix (`chore(deps):` counts the same).
- **Docs and agent config only** — the change touches nothing in
  [What counts as code](#what-counts-as-code).
- **Release prep** — a `chore(release): cut <version> — <headline>` commit touching only
  `CHANGELOG.md` and `VERSION`.
- **A user waiver** — the user explicitly waives the flow for this change. Record it as the PR body's
  first line, in place of the issue link: `Flow waived by the user: <reason>`.

## Linking work to its issue

- **Branch:** `<type>/<issue#>-<slug>`, from the prefixes already in use — `feat`, `fix`, `test`,
  `refactor`, `docs`, `ci`, `build`, `perf`, `security`, `chore`. For example
  `fix/611-adjustment-destination` or `test/575-e2e-fresh-db`. Exempt work with no issue drops the
  number: `docs/dotnet-test-solution`.
- **PR body:** the first line is `Closes #N` when the PR finishes the ticket, or `Part of #N` when it
  is one slice of a spec that stays open, as in `Part of #547 (step 4: …)`. GitHub links an issue only
  from a closing keyword in the body; a number in the PR title alone links nothing.
- **Private issues:** the branch is `<type>/<slug>` and the PR body names no issue. Close the private
  issue by hand after the merge ([issue-tracker.md](issue-tracker.md#which-tracker)).

`/ship` warns when a PR body carries no closing keyword. Outside the exemptions, that warning means
the link above is missing.
