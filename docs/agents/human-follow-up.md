# Human follow-up

Required human actions left by completed agent work follow the `## Procedure` in the installed
skills' `human-follow-up` definition (for example
`.agents/skills/end-session/settings/human-follow-up.md`), with the values below. The completing
agent owns the follow-up before it reports the work complete, including work finished without
`/ship` or `/end-session` (`AGENTS.md`, "Issue tracker"). Reconcile against both trackers: a
private follow-up may cite a public issue, never the reverse. Keep the companion's locator, its
issue and wiki links, and credential values out of public files, issues and PR bodies.

## Follow-up destination

`discover: git -C private remote get-url origin` — a private companion issue for every
independently completable human action, even when the engineering change is public, selected
explicitly with `-R`. **Deployment consolidation:** LeaseBook is not publicly deployed, so every
deployment-related action (authoring, provisioning, credentials, first-deploy verification,
restore drills, account rollout, telemetry and alerting) is appended to the one private issue
titled **Public distribution: deployment and operational readiness** and to its wiki page, never a
new issue, board item or page. That issue stays deferred until the maintainer elects public
distribution, and closes only on distribution with its deployment evidence; no merge closes it.

## Follow-up label

`ready-for-human` (create it if absent), plus `operator` for an action that needs operator access.
`blocked-by-human` goes on a follow-up that other work waits on and on each engineering issue it
blocks; remove a conflicting `ready-for-agent` while blocked (`triage-labels.md`).

## Follow-up board

same as Board — `Track` and `Gate` from the action: operator work `B - operator` / `Operator`;
customer, legal and other external decisions `C - external` / `External`; an action that waits on
a trigger `Deferred trigger` / `Trigger-fired`, with the `deferred-trigger` label. `Status` is the
board's own.

## Follow-up wiki

The private companion's wiki: `discover: git -C private remote get-url origin`, with `.wiki.git`
in place of `.git` (default branch `master`). Index page `human-todo`, linked from `Home`; deployment
steps go only in its `Public-Distribution-Deployment` page. Each procedure links its owning issue,
and the issue links back. Push without asking: `AGENTS.md` requires the procedure published before
agent work is reported complete.
