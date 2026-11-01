<!-- Generated from settings/triage-labels.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: triage-labels

`docs/agents/triage-labels.md` — the labels that say how ready an issue is. A skill's role name is
the GitHub label to apply, so the file points here and holds only what the repository adds. An issue
normally carries one of these, plus whichever of GitHub's `bug`, `enhancement` or `documentation`
says what kind of issue it is.

| Label | Meaning |
| --- | --- |
| `needs-triage` | A maintainer needs to evaluate this issue |
| `needs-info` | Waiting on information or evidence not yet available |
| `ready-for-agent` | Fully specified: an agent can implement it unattended |
| `ready-for-human` | Needs a person: credentials, access, or a decision only they can make |
| `wontfix` | Will not be actioned |

## Extra labels

- **Read by:** end-session
- **Value:** bulleted list, one per label beyond the five: the label, what it means, and which of the
  five it replaces or sits beside.
- **Default:** `none`
- **Discover:** `gh label list`, and `docs/agents/triage-labels.md`. Write the heading only for a
  label the repository already uses for a state the five cannot express.
