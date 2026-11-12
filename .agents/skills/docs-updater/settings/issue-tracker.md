<!-- Generated from settings/issue-tracker.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: issue-tracker

`docs/agents/issue-tracker.md` — where work is tracked. The file usually exists already (from the
`mattpocock/skills` setup flow) with its own sections, which `/triage`, `/to-tickets` and
`/wayfinder` read; these settings are added beside them. Labels are the `triage-labels` topic.

## Public repository

- **Read by:** end-session, what-next
- **Value:** `owner/name` exactly as GitHub spells it (casing can differ from the directory name).
- **Discover:** `gh repo view --json nameWithOwner -q .nameWithOwner`.

## Private issue repository

- **Read by:** end-session, ship, what-next
- **Value:** `none`, a literal `owner/name`, or a `discover:` value — the repository that holds
  issues too sensitive for the public tracker.
- **Default:** the `Private companion`'s repository; `none` without a companion.
- **Discover:** the companion's remote (`git -C private remote get-url origin`) and what
  `AGENTS.md` and existing skills say about private issues. A repository that keeps the name out of
  its public files uses `discover:`.

## Board

- **Read by:** end-session, ship, what-next
- **Value:** `none`, or the GitHub project as `<owner> <number>` (the arguments `gh project` takes),
  or a `discover:` value.
- **Discover:** `gh project list --owner <owner>`, matched to the repository by title, and links to
  `github.com/users/<owner>/projects/<n>` in the repository's docs and skills.

## Board fields

- **Read by:** end-session, what-next
- **Value:** bulleted list, one per field the skills set: field name, its options, and when each
  applies (e.g. `Status: Todo / In Progress / Blocked / Parked / Done`; `Blocked by: <reason>` set
  with `Blocked`). Field and option IDs are looked up at run time, never written here.
- **Discover:** `gh project field-list <number> --owner <owner> --format json`, and existing
  skills and docs for how each field is used.

## Priority order

- **Read by:** what-next
- **Value:** how the `Board` ranks open work, highest first, as an ordered list of keys; a later
  key breaks the earlier one's ties. A key is a single-select field with its options in rank order
  (`Priority`: `P0` / `P1` / `P2` / `P3`), a number field and its direction (`Rank`, lowest first),
  or `board order`, the board's manual order from the top (e.g. `Priority`, then `board order`).
  `none` when nothing ranks the work.
- **Default:** `none` with `Board: none`.
- **Discover:** the `Board fields` setting, for a field it calls a priority, rank, order or stage,
  and any rule there that the board's manual order follows. A field the board's workflows set on
  their own (`Status`) is not a rank, nor is one that says why work waits (`Gate`).

## New work

- **Read by:** end-session
- **Value:** how work found during a session, and not yet committed to, is recorded: `issue` (an
  issue, labelled and added to the `Board`) or `draft item` (a draft item on the `Board`, which
  becomes an issue only when the work is committed to, by the rule the repository records).
- **Default:** `issue`
- **Discover:** `AGENTS.md`, the ADRs and this file's own sections, for a rule that keeps
  uncommitted work as board drafts. Write the heading only for `draft item`.

## Security findings

- **Read by:** docs-updater, end-session, what-next
- **Value:** where a security finding is recorded before its fix ships, and what may point at it.
  Never a public issue.
- **Default:** a draft repository security advisory on the `Public repository`. A board item or a
  private follow-up issue may carry only a sanitized reference to it; no public issue, PR, commit,
  wiki page or memory body holds its detail before the fix ships, and drafting it never authorizes
  publishing it.
- **Discover:** `SECURITY.md`, `AGENTS.md`, and existing skills' security sections. Write the
  heading only for a rule the default lacks, such as where an accepted risk is recorded.
