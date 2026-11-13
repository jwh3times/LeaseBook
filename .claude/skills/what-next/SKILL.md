---
# GENERATED — do not edit. Source: .agents/skills/what-next/SKILL.md — regenerate with 'node scripts/sync-agents.mjs'.
name: what-next
description: Report what to work on next in this repository — an active handoff waiting to be resumed, work already in flight, ready issues ranked the way the board ranks them, what needs the user, what needs shaping first, and blocks that have quietly cleared — and recommend the one command to run. Read-only. Use when the user asks what to work on next, what is ready or what to pick up, or when a phase ends with no next piece of work chosen.
argument-hint: "A focus: agent, human, bugs, or a label, area or field value (optional)"
metadata:
  settings: issue-tracker triage-labels handoffs dev-flow
---

# What next

Scan this repository's trackers and board, rank the open work, and say what to pick up next. The
development flow's routing table says which command takes each kind of work; this skill finds the
work, ranks it, and names the command. It recommends and stops: the human starts every phase.

**Read-only.** Never change a label, field, assignee, issue or PR, and never claim an issue or a
handoff: `/implement`, `/lets-go` and the other phase commands do that when the work begins.
Tracker problems the scan finds are reported, for the user or `end-session` to fix. The settings
files step 0 may write are the only files this skill ever changes; reading the handoff map may also
fetch a copy of the map and its document into the local Handoffs folder.

Run shell snippets in bash (Git Bash on Windows).

## 0. Settings

This skill reads `docs/agents/issue-tracker.md`, `docs/agents/triage-labels.md` and
`docs/agents/handoffs.md`. Follow `.agents/skills/what-next/settings/README.md` to read them and
complete any that are missing; the definitions sit beside it. It never sets up the handoffs machine
config (`.agents/skills/handoff/machine-config.md`): that writes a file, so a machine without one
skips the handoff check (step 1). Then read:

- `.agents/skills/what-next/settings/dev-flow.md` `## Procedure`: **The gate** (what makes an issue
  ready), **Routing** (which command takes which work, and what "nobody has started" means) and
  **Private work**. Read `docs/agents/dev-flow.md` too, for what this repository adds to the flow.
- The other sections of `docs/agents/issue-tracker.md` (such as **Blocking** or **Wayfinding
  operations**): they say how this repository records blockers and maps.

From the settings, work out before collecting:

- **Trackers:** the `Public repository`, and the `Private issue repository` unless `none`. A
  `discover:` value is followed now; the private tracker's name stays out of anything public.
- **Board meanings:** from `Board fields` and `Extra labels`, which values mean in progress, which
  mean parked or deferred, which mean waiting on something outside the code, which mean waiting on
  the owner, and which mean a spec or research must come first. A repository's own word for a state
  counts (`Deferred`, `Deferred trigger`, `P3 - parked`, `deferred`, `blocked-by-human`).

## 1. Collect

```bash
me="$(gh api user --jq .login)"
repository="<Public repository>"   # owner/name
```

`board_owner` and `board_number` below are the two words of the `Board` setting.

**The active handoff**, through the script the `handoff` skill installs, as `lets-go` reads it:

```bash
node .agents/skills/handoff/handoff-map.mjs get --key <Map key>
```

- **`file` is null** (or `inMap` is false): no handoff waits for this repository.
- **`file` is set and `exists` is true**: a handoff waits to be resumed. Read the document's
  **Where you are** and first **Next step** for the report; do not act on them.
- **`file` is set and `exists` is false**: the document has not reached this machine yet (the
  `note` says why). Report it as pending.
- **`conflicts` not empty**: report that the map has conflicting copies, which `lets-go` resolves
  with the user.
- **An error**, a missing script, or no machine config (the error names `config`): the handoff
  check was not read. Never run `config --write` from here.

**Open issues**, once per tracker, with `owner` and `name` split from its `owner/name`:

```bash
gh api graphql --paginate -f owner="$owner" -f name="$name" -f query='
query($owner: String!, $name: String!, $endCursor: String) {
  repository(owner: $owner, name: $name) {
    issues(states: OPEN, first: 100, after: $endCursor) {
      pageInfo { hasNextPage endCursor }
      nodes {
        number title createdAt updatedAt url
        labels(first: 20) { nodes { name } }
        assignees(first: 5) { nodes { login } }
        issueDependenciesSummary { blockedBy totalBlockedBy blocking }
        closedByPullRequestsReferences(first: 5, includeClosedPrs: false) { nodes { number isDraft } }
        parent { number title }
      }
    }
  }
}' --jq '.data.repository.issues.nodes[]'
```

`issueDependenciesSummary.blockedBy` counts open blockers only (0 means no native blocker holds it),
`totalBlockedBy` counts closed ones too, and `blocking` counts the open issues waiting on this one.

**Open PRs** on the `Public repository`:

```bash
gh pr list -R "$repository" --state open --limit 200 \
  --json number,title,author,isDraft,reviewDecision,closingIssuesReferences,statusCheckRollup,headRefName,createdAt
```

**The board**, unless `Board` is `none`:

```bash
gh project item-list "$board_number" --owner "$board_owner" --format json --limit 1000
```

Items come back in the board's manual order, top first, which is what `board order` in
`Priority order` reads. Each item's field values are keys named by the field in lower case
(`priority`, `blocked by`); `content.number` and `content.repository` join it to an issue, and an
item whose `content.type` is `DraftIssue` is a board draft. Reading a board needs the token's
`read:project` scope (`gh auth status`). Without it, or on any other failure, rank without the
board and say so in the report; never stop over it.

**Security findings**, when the `Security findings` setting is the default (draft advisories):

```bash
gh api "repos/$repository/security-advisories?state=draft" --jq length
```

A count only, never a title or summary. An error prints `not readable`, never `0`.

## 2. Classify

Put each open issue in exactly one bucket, taking the first that fits:

1. **Someone else's** — assigned only to people other than `me`. Count it; do not list it.
2. **Finish first** — named by an open PR (the issue's `closedByPullRequestsReferences`, or a PR's
   `closingIssuesReferences`); or assigned to `me`, or in an in-progress board state, and not
   waiting (4). Assigned work that is waiting goes to **Waiting**, and the report's hygiene line
   names it.
3. **Parked** — `wontfix`, or a label or board value that means parked or deferred. Count only.
4. **Waiting** — an open native blocker (`blockedBy` above 0); the `blocked` label or an extra label
   meaning the same; a board field naming a blocker outside the project (a cleared value such as
   `Nothing`, `None` or `Ready — …` names none); a gate value that means waiting on something
   outside the code; or `needs-info`. Count by reason; do not list. A stale block (below) is not
   waiting.
5. **Needs you** — `ready-for-human`, `blocked-by-human` or a like extra label, or a gate value
   that waits on the owner's decision or action.
6. **Ready for an agent** — ready by **The gate**: route a `bug` to `/diagnosing-bugs`, and a
   `ready-for-agent` issue to `/implement`.
7. **Needs shaping** — everything else, with its route from **Routing**: no triage label or
   `needs-triage` → `/triage`; a gate value calling for a spec or research → `/grill-with-docs`, then
   `/to-spec`; a `wayfinder:map` → `/wayfinder` (group its child tickets, found by `parent`, under
   it rather than listing them). With `New work: draft item`, count the board's drafts here too.

**Stale blocks.** An issue carrying a blocked label or gate value, with no open native blocker
(`blockedBy` 0, `totalBlockedBy` above 0) and no outside blocker in a board field, was blocked only
by issues that have since closed. Classify it as though unblocked, mark it `stale block`, and list
it in the report's hygiene line. It is often the best pick, and only a tracker edit stands between
it and the queue.

**The gate needs the body.** Labels alone cannot show it: a `ready-for-agent` label on a free-form
body is a raw issue, and `/triage` posts its brief as a comment. Check only the items the report
will list, as ranking reaches them: `gh issue view <n> -R <tracker> --json body,comments`. One that
fails moves to **Needs shaping**, routed to `/triage`.

**PRs.** Each of `me`'s open PRs goes in **Finish first**, with its state: checks failing, changes
requested, approved and waiting for a merge, a draft, or waiting for review. Another author's
non-draft PR, a dependency bot's included, goes there too, as a review or merge to make, with its
checks' state.

**Private work** is classified and routed like the rest, and marked `[private]` in the report.

**An active handoff** heads **Finish first**, routed to `/lets-go`: another session parked its work
for this one to resume, and its document names the next step. A pending one (its document not here
yet) is listed there too, with no route. The map does not record which machine wrote a handoff, so
the report names the document and its date: a handoff this machine wrote is waiting for the other
one, and is left alone.

## 3. Rank

Within each bucket, order by:

1. `Priority order`, key by key. An item missing a key's value sorts after every item that has one.
2. The open issues it unblocks (`blocking`), most first.
3. Age (`createdAt`), oldest first.

With `Priority order: none`, or the board unreadable, rank by 2 and 3 alone and say so.
**Finish first** goes: the active handoff, then PRs with failing checks or changes requested, then
approved PRs, then PRs waiting for review, then assigned issues with no PR, then drafts.

**The recommendation** is, in order:

1. The top of **Finish first**, unless it holds only drafts or a pending handoff. An active handoff
   whose document is here is resumed with `/lets-go`, which brings the checkout to its state before
   anything else starts. A green PR with nothing requested is finished by the user's review and
   merge, which the recommendation names; it never merges.
2. Otherwise the higher-ranked of the tops of **Ready for an agent** and **Needs you**, comparing
   by the same keys. On a tie, the **Needs you** item: no agent can take it, and it may unblock
   others.
3. Otherwise the top of **Needs shaping**.
4. Otherwise nothing: say the queue is empty, with the **Waiting** counts.

## 4. Report

An argument narrows the report: `agent` keeps **Finish first** and **Ready for an agent**, `human`
keeps **Finish first** and **Needs you**, `bugs` keeps `bug` issues, and anything else keeps the
items whose labels, board values or titles match it. The recommendation comes from what is kept.

Refer to each item by its title with its number (`Add the lap filter (#212)`), never by number
alone. List at most ten items per bucket and count the rest. Leave out an empty bucket.

```markdown
## What next: <repository>

**Next:** <title> (#<n>) <[private]> — run `<command> <n>`. <One line: why this, above the rest.>

### Finish first
- Active handoff `<file>` (<date>) — <its first next step> — `/lets-go`, unless this machine wrote
  it <or: pending, not on this machine yet>
- <title> (#<n>) — <PR state, or "assigned, no PR"> — <what finishing takes>

### Ready for an agent (<count>)
1. <title> (#<n>) — <rank values>; unblocks <k> — `/implement` or `/diagnosing-bugs`

### Needs you (<count>)
1. <title> (#<n>) — <the decision or action owed>

### Needs shaping (<count>)
1. <title> (#<n>) — <why it is not ready> — `<route>`

### Waiting
<n> blocked by open issues, <n> on outside parties, <n> needing info, <n> parked, <n> others'
work.<, <n> draft security advisories open.>

**Tracker hygiene:** <stale blocks, assigned work that is waiting, ready-for-agent labels on raw
issues, open items with no `Priority order` value, conflicting state labels — or "none found">.
**Not read:** <the handoff map, the board, the private tracker or advisories, with why — or leave
the line out>.
```

Then stop. Say which command the recommendation runs, and wait: the user starts the next phase.
Running in a subagent or a non-interactive run, return the report to the caller instead.
