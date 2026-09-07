# Triage Labels

- **Audience:** Coding agents and maintainers configuring engineering skills
- **Status:** Living configuration
- **Owner:** Maintainers
- **Last reviewed:** 2026-09-07

The skills speak in terms of five canonical triage roles. This repository uses the role names
verbatim as its GitHub label strings, so a skill's role name is the label to apply.

| Label             | Meaning                                                                |
| ----------------- | ---------------------------------------------------------------------- |
| `needs-triage`    | Maintainer needs to evaluate this issue                                |
| `needs-info`      | Waiting on the reporter for more information                           |
| `ready-for-agent` | Fully specified; an AFK agent can implement it — the "AFK-ready" role  |
| `ready-for-human` | Requires human implementation, usually a product or fiduciary decision |
| `wontfix`         | Will not be actioned                                                   |

When a skill names a role — "apply the AFK-ready triage label" — apply the matching label above.

Two label families sit outside triage and are not interchangeable with it: `wayfinder:map` and
`wayfinder:<type>` mark architecture-map tickets (see [issue-tracker.md](issue-tracker.md)), and
GitHub's own `bug`/`enhancement` labels classify the issue rather than its readiness. An issue
normally carries one triage label plus whichever of those apply.

`blocked-by-human` is a dependency label, separate from triage: the issue cannot complete until a
human action, decision, access, or external evidence is supplied. Apply it to human-owned actions
and dependent engineering work; `ready-for-human` identifies who performs the action. Remove
`ready-for-agent` while blocked, and reassess labels when the required evidence arrives. Keep
`operator` for work requiring operator access. Follow the
[human-action handoff](issue-tracker.md#required-human-action-handoff) for agent-completed work.
