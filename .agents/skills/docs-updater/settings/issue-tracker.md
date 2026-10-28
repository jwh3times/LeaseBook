<!-- Generated from settings/issue-tracker.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: issue-tracker

`docs/agents/issue-tracker.md` — where work is tracked. The file usually exists already (from the
`mattpocock/skills` setup flow) with its own sections; these settings are added beside them. Label
names stay in `docs/agents/triage-labels.md`, or in this file's own triage-labels section where a
repository keeps them there.

## Public repository

- **Read by:** end-session
- **Value:** `owner/name` exactly as GitHub spells it (casing can differ from the directory name).
- **Discover:** `gh repo view --json nameWithOwner -q .nameWithOwner`.

## Private issue repository

- **Read by:** end-session, ship
- **Value:** `none`, a literal `owner/name`, or a `discover:` value — the repository that holds
  issues too sensitive for the public tracker.
- **Discover:** the companion's remote (`git -C private remote get-url origin`) and what
  `AGENTS.md` and existing skills say about private issues. A repository that keeps the name out of
  its public files uses `discover:`.

## Board

- **Read by:** end-session, ship
- **Value:** `none`, or the GitHub project as `<owner> <number>` (the arguments `gh project` takes),
  or a `discover:` value.
- **Discover:** `gh project list --owner <owner>`, matched to the repository by title, and links to
  `github.com/users/<owner>/projects/<n>` in the repository's docs and skills.

## Board fields

- **Read by:** end-session
- **Value:** bulleted list, one per field the skills set: field name, its options, and when each
  applies (e.g. `Status: Todo / In Progress / Blocked / Parked / Done`; `Blocked by: <reason>` set
  with `Blocked`). Field and option IDs are looked up at run time, never written here.
- **Discover:** `gh project field-list <number> --owner <owner> --format json`, and existing
  skills and docs for how each field is used.

## New work

- **Read by:** end-session
- **Value:** how work found during a session, and not yet committed to, is recorded: `issue` (an
  issue, labelled and added to the `Board`) or `draft item` (a draft item on the `Board`, which
  becomes an issue only when the work is committed to, by the rule the repository records).
- **Discover:** `AGENTS.md`, the ADRs and this file's own sections, for a rule that keeps
  uncommitted work as board drafts; `issue` when nothing says so.

## Security findings

- **Read by:** docs-updater, end-session
- **Value:** where a security finding is recorded before its fix ships — a draft repository
  advisory (optionally with a draft board item), a private issue labelled `security`, a named
  private document, or the process in `SECURITY.md`. Never a public issue.
- **Discover:** `SECURITY.md`, `AGENTS.md`, and existing skills' security sections.
