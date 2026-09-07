# Issue tracker: GitHub

- **Audience:** Coding agents and maintainers configuring engineering skills
- **Status:** Living configuration
- **Owner:** Maintainers
- **Last reviewed:** 2026-09-07

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

**Private follow-up exception to the public default:** all required human actions arising from
agent-completed work belong in private follow-up issues, even when the engineering change is public.
The completing agent owns this handoff before reporting completion, including work completed without
`/ship` or `/end-session`. This includes operator execution, access, deployment verification, customer
input, legal review, product decisions, and account-holder enrollment or recovery. Complete authorized
agent-executable work first; routine engineering is not a human follow-up.

1. **Create or update a private follow-up issue for every independently completable human action.**
   Search both trackers first. Reuse an existing private issue when it owns the same action; if a
   public issue already exists, cite it from the private follow-up without copying its status or
   creating a reverse public reference. State the originating change/PR, why a human is required,
   responsible role (named assignee when known), prerequisites, ordered actions, and observable
   acceptance evidence. Group steps only when they share an owner and completion event.
2. **Add each follow-up to the shared LeaseBook project board (project 3).** This is the board for
   both repositories, not a separate private-only project. Infer the private repository from its
   checkout remote. Use `gh project item-add 3 --owner jwh3times --url <private-issue-url>` and set
   `Track` and `Gate` from the board's current field options: operator work uses the operator track
   and gate; customer/legal/external decisions use the external track and gate. Read field IDs
   rather than hardcoding them. Apply `ready-for-human` to human-owned actions and `blocked-by-human`
   to work that cannot complete until human action or evidence arrives; keep `operator` where
   applicable. Remove conflicting `ready-for-agent` labels while blocked. Record dependencies where
   supported without exposing private references on public surfaces.
3. **Publish step-by-step instructions in the private repository wiki.** Create or update the
   procedure and link it from `human-todo` and the wiki home. Include audience/owner, purpose,
   prerequisites and required access, release/environment assumptions, numbered commands or exact
   UI steps, expected results, verification evidence, and applicable failure/retry/rollback or
   escalation instructions. Identify unverified live steps explicitly. Use secret placeholders
   and approved secret sources; never publish credentials, recovery material, or tokens. A wizard
   may assist execution but does not replace the wiki procedure.
4. **Link and verify both artifacts.** The private issue links to the exact wiki procedure/section;
   the wiki links back to its owning issue. Issues and board fields own work status; `human-todo`
   is the procedure index, not a duplicate status tracker. Verify issue labels, board membership
   and fields, published wiki content, and page/section links. Keep follow-ups open until the
   required human evidence exists; a merged PR or local tests alone cannot close them.
5. **Report the handoff.** Give the user the private issue and wiki URLs and identify remaining
   human actions. If none exist, say so. If private tracker, board, or wiki access is unavailable,
   prepare issue text and instructions in confidential local storage, report the exact publication
   blocker and minimum access needed, and mark the handoff incomplete. Do not silently skip it or
   substitute a public issue, transcript-only list, or local draft for verified publication.

Keep private repository locators and private artifact URLs out of public repository files, commits,
PR descriptions, and issues. The private follow-up may cite public engineering work.

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
