<!-- Generated from settings/human-follow-up.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: human-follow-up

`docs/agents/human-follow-up.md` — where a human action left by completed agent work is recorded,
and the procedure for recording it. Older names for this file (`human-actions.md`,
`human-followups.md`, `docs/human-followups.md`, a section of `issue-tracker.md`) are where step 0
finds today's values.

## Follow-up destination

- **Read by:** docs-updater, end-session, ship
- **Value:** where each follow-up becomes trackable: issues in a repository (`owner/name`, or a
  `discover:` value), or a checkbox in one public tracking issue (`owner/name#n`).
- **Discover:** the older files above, and existing skills' human follow-up sections.

## Follow-up label

- **Read by:** docs-updater, end-session, ship
- **Value:** the label a follow-up issue carries (`ready-for-human`, `human-action-required`, …),
  or `none` for a tracking-issue checkbox.
- **Discover:** as above, and the triage label vocabulary (`docs/agents/triage-labels.md`, or its
  section of `docs/agents/issue-tracker.md`).

## Follow-up board

- **Read by:** docs-updater, end-session, ship
- **Value:** `none`, `same as Board` (the `Board` setting in `issue-tracker.md`), or a separate
  project as `<owner> <number>` or a `discover:` value, with the fields to set.
- **Discover:** as above.

## Follow-up wiki

- **Read by:** docs-updater, end-session, ship
- **Value:** `none`, or the wiki that holds step-by-step instructions: its repository and index
  page (e.g. the destination repository's wiki, index `human-todo`), plus whether pushing to it is
  pre-authorized (`push without asking` or `ask before pushing`).
- **Discover:** as above. A wiki's Git remote is `https://github.com/<owner>/<name>.wiki.git`.

## Procedure

Run this whenever completed work leaves a required human action: account or access setup,
credentials, a dashboard or operator change, a decision, a manual acceptance check, evidence only a
human can produce. The agent that completed the work owns the follow-up; other skills verify it. A
line in a final message is not a follow-up.

1. **Inventory.** List every required human action the work left. Do the ones the agent can do
   within its authorization instead. Record a conditional action with its exact trigger, and treat
   it as pending, not blocking, until the trigger occurs.
2. **Reconcile.** Search the destination's issues (closed ones too), the board, and the wiki for the
   same action. Update a matching open follow-up instead of duplicating it; reopen only when the
   same obligation recurs, otherwise link the old issue as context.
3. **Record.** Create or update one follow-up per independently completable action: the originating
   PR or commit, what the agent already did, the remaining action, why a human is needed, the
   responsible role (never a guessed assignee), which environment, prerequisites, the acceptance
   criteria and where the completion evidence is kept, and the wiki link. A decision records its
   options and the consequences of each. Write multi-line
   bodies to a temporary file and pass `--body-file`. Select the destination repository explicitly
   with `-R`, even from the public checkout. Apply the follow-up label, creating it if the
   repository lacks it.
4. **Board.** Add the issue to the follow-up board and set its fields from current evidence:
   actionable, blocked by a named prerequisite, or parked until a conditional trigger. Look up
   field and option IDs; never assume them.
5. **Instructions.** Publish numbered steps on the wiki: access needed, exact commands or verified
   UI navigation, expected results, how to verify completion, and recovery where relevant. Mark
   uncertainty about a UI instead of inventing clicks. Link the issue and source work, and index the
   page from the wiki's index page. Preserve other pages and concurrent edits when pushing; a local
   wiki commit without a successful push is not publication.
6. **Verify.** Read back the issue, label, board fields, and wiki page, and check the links run both
   ways. Close a follow-up only on evidence the action was done (or a decision it no longer
   applies), never because the implementation merged, recording the outcome without deleting
   reusable instructions.

Keep secrets, tokens, account identifiers, and credential values out of issues and wiki pages; refer
to the secret store instead. A security remediation follows the `Security findings` route in
`issue-tracker.md`; its follow-up links to it without restating it, and drafting an advisory never
authorizes publishing it or requesting a CVE. When a destination is a
`discover:` value, report publication status without its identifiers.

If the destination, board, or wiki is unreachable, complete every reachable part, keep a sanitized
draft in a private location, and report exactly what is unpublished and why — never as complete,
and never by substituting a public issue. If no human action remains, say so and create nothing.
