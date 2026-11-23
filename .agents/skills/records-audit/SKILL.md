---
name: records-audit
description: Audit this repository's records against each other, read-only — the board against the issues (closed but not Done, open but Done, missing fields, stale In Progress, blockers that closed, drafts, overdue and due-soon dates), the trackers (stale needs-triage, blocked issues with nothing open blocking them, human follow-ups with a deadline only in the title), the human follow-up wiki against its issues, and the changelog against the tags — and name the command or skill that fixes each finding. Writes only its dated report. Use weekly, when the user asks to audit the board or the records, or when what-next says the last audit is more than a week old.
argument-hint: "Optional: board, issues, follow-ups or changelog, to run only those checks"
model: inherit
metadata:
  settings: issue-tracker triage-labels human-follow-up releases workspace
---

# Records audit

The board, the issues, the follow-up wiki and the changelog each record the same work, and they
drift apart quietly: an issue closes and its board item stays In Progress, a blocker closes and
the issue it blocked keeps its label, a deadline lives only in an issue's title. This skill finds
those disagreements and says what fixes each one. It fixes nothing itself.

**Announce at start:** "I'm using the records-audit skill to audit this repository's records."

Run shell snippets in bash (Git Bash on Windows). It runs in the session, not in a subagent: it may
need the user to sign in to `gh` (the `read:project` scope) or to a secret store, and it reads the
private tracker and wiki, which a cloud routine cannot reach. It runs on the session's model.

**Read-only.** Never change an issue, label, board item, field, wiki page, tag or file, and never
commit or push. Board reads go through `.agents/skills/end-session/board.mjs audit` and
`gh project item-list`; `board.mjs`'s writing commands are never run here. Clones of a wiki go to
a temporary folder and are deleted after. Apart from the settings files step 0 may write, the one
file it writes is its report (step 6), outside the working tree, so `git status` reads the same
before and after. Every finding names its fix,
for the user, `end-session` or the skill named to apply.

**Private findings stay private.** Findings from the `Private issue repository`, the companion and
a private wiki go in the report and the conversation, never in a public issue, PR, commit or
comment.

## 0. Settings

This skill reads `docs/agents/issue-tracker.md`, `docs/agents/triage-labels.md`,
`docs/agents/human-follow-up.md`, `docs/agents/releases.md` and `docs/agents/workspace.md`. Follow
`.agents/skills/records-audit/settings/README.md` to read them and complete any that are missing;
the definitions sit beside it. Resolve each `discover:` value once, here.

Check `gh auth status`: board reads need `read:project`. Without it, ask the user to run
`gh auth refresh -h github.com -s read:project` in a real terminal; when they decline, skip the
board and say so.

An argument (`board`, `issues`, `follow-ups`, `changelog`) runs only those sections.

## 1. Board

With a `Board`, `<board>` its two words:

```bash
node .agents/skills/end-session/board.mjs audit --board "<board>" --repo "<Public repository>" [--repo "<Private issue repository>"] [--status-field "<field>" --done "<option>"]
gh project item-list <number> --owner <owner> --format json --limit 1000
```

Name the status field and done option as `end-session` step 3 does, from `Board fields`. Add
`--due-field "<name>"` when `Board fields` names the date field another way than `Due`.

| Finding | From | Fix |
| --- | --- | --- |
| Closed, not done | `closedNotDone` | `board.mjs set ... --set "Status=Done"` (`end-session`) |
| Open, marked done | `openDone` | reopen the work's status, or close the issue if it is done |
| No status | `noStatus` | set its status (`end-session`) |
| Open issue not on the board | `missing` | `board.mjs file` (`end-session`) |
| Overdue | `overdue` | the user: do it, or move `Due` with the reason |
| Due within 7 days | `dueSoon` | the user, as a reminder |
| Missing a required field | item-list | `board.mjs set` with the value `Board fields` describes |
| In Progress, no open PR, no assignee | item-list and step 2's issues | back to its waiting state, or assign it (`end-session`) |
| A blocker field or Gate naming a closed issue | item-list, `#N` in its value | clear the field and move the item on (`end-session`) |
| Draft items | item-list, `content.type` `DraftIssue` | with `New work: issue`, turn each into an issue; with `draft item`, only list them |

A field is required when `Board fields` says every item, or every item in a state, carries it
(`Blocked by` with `Blocked`). Each `#N` (or `owner/name#N`) in a text field is read
(`gh issue view N -R <repository> --json state`).

## 2. Issues

In each tracker (the `Public repository`, and the `Private issue repository` unless `none`), with
`-R` always:

```bash
gh api graphql --paginate -f owner="$owner" -f name="$name" -f query='
query($owner: String!, $name: String!, $endCursor: String) {
  repository(owner: $owner, name: $name) {
    issues(states: OPEN, first: 100, after: $endCursor) {
      pageInfo { hasNextPage endCursor }
      nodes {
        number title createdAt url
        labels(first: 20) { nodes { name } }
        assignees(first: 5) { nodes { login } }
        issueDependenciesSummary { blockedBy totalBlockedBy }
        closedByPullRequestsReferences(first: 5, includeClosedPrs: false) { nodes { number } }
      }
    }
  }
}' --jq '.data.repository.issues.nodes[]'
```

| Finding | Test | Fix |
| --- | --- | --- |
| Untriaged for more than 14 days | `needs-triage`, or no triage label, and `createdAt` over 14 days ago | `/triage` |
| Blocked by nothing open | the `blocked` label (or an `Extra labels` equivalent), `blockedBy` 0 and `totalBlockedBy` above 0 | remove the label and re-triage (`end-session`) |
| A deadline only in the title | the `Follow-up label`, a date in the title (`2026-12-21`, `Dec 21`, `by Friday`), and no `Due` on its board item | set `Due` with `board.mjs set` (`end-session`) |

## 3. Human follow-ups

With a `Follow-up wiki`, clone it read-only into a temporary folder
(`git clone --depth 1 https://github.com/<owner>/<name>.wiki.git "$wiki_dir"`) and read its index
page. For each follow-up issue (the `Follow-up label` in the `Follow-up destination`, open, and
closed in the last 30 days):

- its wiki page exists, the issue links to it and it links back to the issue;
- the index lists it, with its checkbox matching the issue: unchecked while open, checked or moved
  to a done list once closed;
- an open issue whose page says it is done, or a closed one whose page still lists steps to take,
  is a mismatch.

The fix is the `## Procedure` in `.agents/skills/records-audit/settings/human-follow-up.md`, step
6 (Verify), which `end-session` runs. With `Follow-up wiki: none`, check instead that each open
follow-up's body has `## Steps` and `## Done when`. Delete the clone when done.

## 4. Changelog

With `Scheme: stamp-on-tag` (and for `per-merge`, whose sections are written by `ship`):

```bash
git fetch -q --tags origin
git tag -l 'v*' --sort=-v:refname
MSYS_NO_PATHCONV=1 git show origin/main:CHANGELOG.md
```

`MSYS_NO_PATHCONV=1` keeps Git Bash from rewriting the `ref:path` argument as a Windows path; it
changes nothing elsewhere.

- Every three-part `v*` tag not matched by a `Skipped tags` **Ignore** pattern has a
  `## [x.y.z]` section; a tag with none is a finding, fixed by a PR adding the section, as `ship`'s
  backfill writes one.
- A section with no tag is a finding unless it is `Unreleased`.
- Where the file ends in link references (`[x.y.z]: https://...`), each version has one, each
  compares the right pair of tags, and `[Unreleased]` compares from the newest tag. A stale link is
  fixed in the same kind of PR.
- With `stamp-on-tag`, a fragment in `changelog.d/` on `main` that is older than the newest tag was
  never stamped: name it and the merge that added it.

## 5. Report

```markdown
## Records audit: <repository>, <date>

| Area | Findings |
| --- | --- |
| Board | <n> |
| Issues | <n> |
| Human follow-ups | <n> |
| Changelog | <n> |

### <Area>
- <the record> — <what disagrees with what> — **fix:** <the command or skill>

**Overdue:** <items, oldest first, with their dates>, or none.
**Not read:** <the board, the private tracker, the wiki, with why>, or leave the line out.
```

Each finding is one the user can check by hand: name the item, the two records that disagree, and
the values each holds.

## 6. Save the report

Write the report to the repository's git folder, which is never committed and is shared by its
worktrees, so `what-next` can tell how old the last audit is:

```bash
audit_dir="$(git rev-parse --path-format=absolute --git-common-dir)/records-audit"
mkdir -p "$audit_dir"
```

Name it `<YYYY-MM-DD>.md` for today, replacing a report from earlier the same day. It holds private
findings, so it stays on this machine: never copy it into the tree, the companion or a public place.

## Weekly

Run `/records-audit` by hand once a week, or rerun it weekly with the harness's recurring-task
command where it has one (in Claude Code, `/loop 7d /records-audit` in a long-lived session).
`what-next` says when the newest report is more than seven days old, or when there is none.
