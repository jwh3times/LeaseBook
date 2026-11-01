# Triage Labels

- **Audience:** Coding agents and maintainers configuring engineering skills
- **Status:** Living configuration
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-31

The labels and what they mean are in `.agents/skills/ship/settings/triage-labels.md`. A skill's role name is the GitHub label to apply.

## Extra labels

- `blocked-by-human`: a dependency, sitting beside the five. The issue cannot complete until a human
  action, decision, access, or external evidence is supplied. Apply it to human-owned actions and
  dependent engineering work; `ready-for-human` identifies who performs the action. Remove
  `ready-for-agent` while blocked, and reassess labels when the required evidence arrives.
- `operator`: work requiring operator access, sitting beside the five.

Follow the [human-action handoff](issue-tracker.md#required-human-action-handoff) for agent-completed
work.

## Other label families

`wayfinder:map` and `wayfinder:<type>` mark architecture-map tickets (see
[issue-tracker.md](issue-tracker.md)), and are not interchangeable with triage.
