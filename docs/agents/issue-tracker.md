# Issue tracker: GitHub

- **Audience:** Coding agents and maintainers configuring engineering skills
- **Status:** Living configuration
- **Owner:** Maintainers
- **Last reviewed:** 2026-09-09

Issues and specs for this repo live as GitHub issues. Use the `gh` CLI for all operations.

> **Extended beyond the installer template.** The "Which tracker" and "The one rule" sections below
> are LeaseBook-specific. Re-running `setup-matt-pocock-skills` rewrites this file from the generic
> template and would drop them — re-apply them if that ever happens.

## Conventions

- **Create an issue**: `gh issue create --title "..." --body "..."`. Use a heredoc for multi-line bodies.
- **Read an issue**: `gh issue view <number> --comments`, filtering comments by `jq` and also fetching labels.
- **List issues**: `gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'` with appropriate `--label` and `--state` filters.
- **Comment on an issue**: `gh issue comment <number> --body "..."`
- **Apply / remove labels**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close**: `gh issue close <number> --comment "..."`

## Which tracker

Route engineering work by **whether the text is safe in public git history**. Required human
follow-ups use the private tracker regardless of the engineering change's visibility.

|                      | Repo                             | Holds                                                                                         |
| -------------------- | -------------------------------- | --------------------------------------------------------------------------------------------- |
| **Public** (default) | `jwh3times/LeaseBook`            | Anything a public PR closes: engineering work, bugs, architecture questions, deployment steps |
| **Private**          | the private companion repository | Confidential work and required human follow-ups from agent-completed work                     |

The confidential items mostly close on events in the world rather than on merged PRs, so the split
rarely forces a cross-repo link. **Never reference a private issue from a public PR, commit, or
issue** — the reference itself leaks its existence.

`gh` infers the public repo from `git remote -v` inside the clone. For the private tracker, run `gh`
from inside the `private/` checkout so it infers that repository the same way. **Do not write the
private locator into this tree** - `AGENTS.md` deliberately keeps it, and the bootstrap details,
outside the public repository.

Both trackers share one board: `gh project view 3 --owner jwh3times`. Its `Track` field
(A-closed / B-operator / C-external / Discretionary / Deferred trigger) and `Gate` field
(External / Operator / Trigger-fired / None) carry sequencing. A private board may contain public
issues — the board is a _view_, and confidentiality stays at the issue level.

## The one rule

**An issue states its own question. It never restates another issue's status.**

Relationships go in task lists and `Part of #N`; GitHub renders child state live, so a parent stays
current with nobody maintaining it. Status prose in a parent body is how the Markdown trackers this
replaced went stale, and it goes stale in an issue for exactly the same reason.

Infer the repo from `git remote -v` — `gh` does this automatically when run inside a clone.

## Required human-action handoff

Every required human action left by agent-completed work — including the deployment consolidation
into the private **Public distribution: deployment and operational readiness** issue — is recorded
as [Human follow-up](human-follow-up.md) says: its destination, labels, board fields and private
wiki, and the shared procedure the installed skills carry. The completing agent owns the handoff
before it reports the work complete, including work finished without `/ship` or `/end-session`.

## Pull requests as a triage surface

**PRs as a request surface: no.** External PRs are not treated as feature requests here, and
`/triage` skips them. The repository is solo-maintained with Dependabot as the only routine PR
author, so the triage queue is issues only.

Should that change, the `gh pr` equivalents are:

- **Read a PR**: `gh pr view <number> --comments` and `gh pr diff <number>` for the diff.
- **List external PRs for triage**: `gh pr list --state open --json number,title,body,labels,author,authorAssociation,comments` then keep only `authorAssociation` of `CONTRIBUTOR`, `FIRST_TIME_CONTRIBUTOR`, or `NONE` (drop `OWNER`/`MEMBER`/`COLLABORATOR`).
- **Comment / label / close**: `gh pr comment`, `gh pr edit --add-label`/`--remove-label`, `gh pr close`.

GitHub shares one number space across issues and PRs, so a bare `#42` may be either — resolve with `gh pr view 42` and fall back to `gh issue view 42`.

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## When a skill says "fetch the relevant ticket"

Run `gh issue view <number> --comments`.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single issue with **child** issues as tickets.

- **Map**: a single issue labelled `wayfinder:map`, holding the Notes / Decisions-so-far / Fog body. `gh issue create --label wayfinder:map`.
- **Child ticket**: an issue linked to the map as a GitHub sub-issue (`gh api` on the sub-issues endpoint). Where sub-issues aren't enabled, add the child to a task list in the map body and put `Part of #<map>` at the top of the child body. Labels: `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Once claimed, the ticket is assigned to the driving dev.
- **Blocking**: GitHub's **native issue dependencies** — the canonical, UI-visible representation. Add an edge with `gh api --method POST repos/<owner>/<repo>/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`, where `<blocker-db-id>` is the blocker's numeric **database id** (`gh api repos/<owner>/<repo>/issues/<n> --jq .id`, _not_ the `#number` or `node_id`). GitHub reports `issue_dependencies_summary.blocked_by` (open blockers only — the live gate). Where dependencies aren't available, fall back to a `Blocked by: #<n>, #<n>` line at the top of the child body. A ticket is unblocked when every blocker is closed.
- **Frontier query**: list the map's open children (`gh issue list --state open`, scoped to the map's sub-issues / task list), drop any with an open blocker (`issue_dependencies_summary.blocked_by > 0`, or an open issue in the `Blocked by` line) or an assignee; first in map order wins.
- **Claim**: `gh issue edit <n> --add-assignee @me` — the session's first write.
- **Resolve**: `gh issue comment <n> --body "<answer>"`, then `gh issue close <n>`. The map's task list reflects the closure on its own — **do not write the child's status into the map body**, which is what makes maps go stale.

## Public repository

jwh3times/LeaseBook

## Private issue repository

`discover: git -C private remote get-url origin` — the companion's tracker (see "Which tracker"
above): confidential work — compliance and legal engagements,
customer identity, pricing and strategy — and every required human follow-up. A confidential
product decision carries `product-decision`. Never referenced from a public PR, commit or issue;
its issues are closed by hand after the public merge.

## Board

jwh3times 3

## Board fields

- `Track`: `A - closed` / `B - operator` / `C - external` / `Discretionary` / `Deferred trigger`.
  Engineering an agent can take is `Discretionary`; operator work `B - operator`; customer, legal
  and other external decisions `C - external`; work waiting on a named trigger `Deferred trigger`
  (with the `deferred-trigger` label). `A - closed` is the finished Track A sequence: never set it.
- `Gate`: `None` / `Operator` / `External` / `Trigger-fired`, paired with `Track` (`Discretionary`
  → `None`, `B - operator` → `Operator`, `C - external` → `External`, `Deferred trigger` →
  `Trigger-fired`). An item is tracked only once both are set.
- `Status`: `Todo` / `Done`, set by the board's own workflows (added → `Todo`, closed → `Done`);
  `In Progress` is unused. Never set it by hand.
- Blocked work has no field: the `blocked-by-human` label, or a native `blocked_by` dependency on
  the blocking issue, records it (`triage-labels.md`).

## New work

issue — open work lives only in GitHub issues on the board; there are no draft items. Public by
default, in the private tracker when the text is confidential; labelled from `triage-labels.md`
(`needs-triage` unless it is fully specified), added to the board with `Track` and `Gate` set.

## Security findings

The default: a draft repository security advisory, never a public issue, PR, commit or document
while the weakness is unpatched, and never exploitable detail in memory. A report from outside
arrives through private vulnerability reporting (`SECURITY.md`) as one already. A residual risk
accepted by decision is a private companion issue labelled `security` that cites the advisory
without restating it, closed `wontfix` or labelled `deferred-trigger`.
