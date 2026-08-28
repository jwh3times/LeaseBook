# Triage Labels

- **Audience:** Coding agents and maintainers configuring engineering skills
- **Status:** Living configuration
- **Owner:** Maintainers
- **Last reviewed:** 2026-08-28

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
