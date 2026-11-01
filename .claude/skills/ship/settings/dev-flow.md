<!-- Generated from settings/dev-flow.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: dev-flow

`docs/agents/dev-flow.md` — how work reaches a code change. The `## Procedure` below is the same in
every repository, so that file holds only the settings below and anything the repository adds to
the flow. `/ask-matt` maps the flow skills themselves.

## Docs and agent config

- **Read by:** ship
- **Value:** bulleted list of paths that count as docs and agent config, beyond the default.
- **Default:** `*.md` files anywhere, and `.agents/`, `.claude/`, `.codex/` and `skills-lock.json`.
  Every other tracked path is code, including a non-Markdown file under `docs/`. A change touching
  both is code.
- **Discover:** `docs/agents/dev-flow.md` and `AGENTS.md`. Write the heading only for paths the
  default lacks, such as a docs tooling config.

## Branch types

- **Read by:** ship
- **Value:** the `<type>` values a branch may use, beyond the default.
- **Default:** `feat`, `fix`, `refactor`, `test`, `docs`, `ci`, `build`, `perf`, `chore`.
- **Discover:** `git branch -r` and recent merged PRs. Write the heading only for types the default
  lacks.

## Procedure

### The gate

Every code change starts from a **ready issue**. Read it before the first edit to a code path:

```bash
gh issue view <n> --json state,labels,body,comments,assignees
```

It is ready when it is open and one of:

- **`ready-for-agent`**, with a body that is a `/to-spec` spec or a `/to-tickets` ticket whose
  blockers are all closed, or with a `/triage` agent brief. `/triage` posts its brief as a comment,
  so read the comments too.
- **`bug`**, worked through `/diagnosing-bugs`. The issue number is required; a spec is not.

Any other triage label, no triage label, and a `wayfinder:*` map ticket are not ready. A
`ready-for-agent` label on a free-form body is a raw issue. Reading code, running tests and answering
questions need no issue.

Claim the issue by assigning it when work begins: `gh issue edit <n> --add-assignee @me`.

### Private work

A ready issue in the `Private issue repository` counts. Its number and title stay out of the branch,
the PR, commits and public issues, and no public issue is filed to stand in for it. The branch is
`<type>/<slug>` and the PR body has no link line. Close the private issue by hand after the merge.

A security finding is neither: it follows the `Security findings` setting in `issue-tracker.md`.

### Routing

With no ready issue, stop before the first edit, name the command that fits, say in one line why,
and wait for the user to run it.

| Situation | Route |
| --- | --- |
| An idea one session can hold | `/grill-with-docs`, then `/to-spec` (add `/to-tickets` when it spans sessions) |
| A huge, foggy, multi-session effort | `/wayfinder`, then `/to-spec` once the map clears |
| A raw issue someone else filed | `/triage` |
| A ready issue nobody has started | `/implement`, in a fresh context |
| Something broken | File the bug issue (`bug` label), then `/diagnosing-bugs` |

"Nobody has started" means open, with no open blocker, no assignee, and no open PR naming it.
Tickets `/to-spec` and `/to-tickets` publish are already `ready-for-agent`: they go to `/implement`,
not `/triage`.

### Phases

`/wayfinder`, `/grill-with-docs`, `/to-spec`, `/to-tickets`, `/implement` and `/triage` stay
user-invoked: `disable-model-invocation: true` stays in their frontmatter, and the human starts every
phase. At a phase boundary, name the next command, say in one line why, and wait.

Inside a phase, use `/tdd`, `/code-review`, `/diagnosing-bugs`, `/domain-modeling`,
`/codebase-design`, `/prototype` and `/research` whenever they fit. `/prototype` and `/research` may
run before any issue exists: neither is a change bound for `main`.

`/implement` commits to the current branch, and `main` is protected: cut the topic branch before the
first commit. `/handoff` here is the machine handoff, not the portable-file handoff `/ask-matt`
describes; for a prototype or a side task in another directory, write the brief by hand.

### Exemptions

These need no ready issue, but still go through a topic branch and a PR:

- **Dependency bumps:** a dependency bot's PRs, and a hand-made bump that changes only versions,
  with the edits it needs to pass the checks.
- **Docs and agent config only:** every changed path is under the `Docs and agent config` setting.
- **Release prep:** the changelog and version-file changes `ship` writes, on a branch that passed
  the gate or is exempt, or on a release-only branch.
- **User waiver:** the user waives the flow for this change in their own words. The PR body opens
  with `Flow waived by the user: <reason>`, quoting their reason.

### Linking work to its issue

- **Branch:** `<type>/<issue#>-<slug>`, made from `main`, with a type from `Branch types`. Exempt
  work with no issue drops the number. A dependency bot keeps its own branch names, and `/prototype`
  keeps `prototype/<name>`.
- **PR body:** it opens with `Closes #N` when the PR finishes issue `N`, or `Part of #N` when it is
  one slice of a spec that stays open. Each finished issue gets its own keyword: GitHub links only
  the first issue in `Closes #1 and #2`. Put `Refs #N` for other related issues lower down.
- A number in the PR title alone links nothing.
