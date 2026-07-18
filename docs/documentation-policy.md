# Documentation Policy

- **Audience:** Contributors and maintainers
- **Status:** Living policy
- **Owner:** Maintainers
- **Last reviewed:** 2026-07-10

This policy keeps LeaseBook documentation public-safe, navigable, and maintainable. It applies to
Markdown, runbooks, diagrams, planning artifacts, and documentation embedded in source or automation.

## Classifications

| Classification     | Purpose                                                                                   | Location                                       | Maintenance rule                                                    |
| ------------------ | ----------------------------------------------------------------------------------------- | ---------------------------------------------- | ------------------------------------------------------------------- |
| Public living      | Current product, engineering, contributor, and operator guidance                          | Root or `docs/`                                | Update with the behavior it describes                               |
| Public historical  | Accepted decisions and released history                                                   | `docs/adr/`, `CHANGELOG.md`                    | Preserve history; supersede or append instead of rewriting outcomes |
| Private living     | Detailed plans, commercial scope, customer work, security findings, compliance workpapers | `private/`                                     | Gitignored; may inform public-safe projections                      |
| Private historical | Retrospectives, implementation plans, design-session artifacts                            | `private/planning/` or another private archive | Gitignored; do not treat as current behavior                        |
| Generated          | Output derived from code or another canonical input                                       | Beside its consumer                            | Do not hand-edit; document the generation command                   |

Public documents must not contain pricing, customer identity, confidential strategy, active security
findings, private infrastructure values, real credentials, or internal analysis. A secret scanner does
not enforce this broader confidentiality boundary.

## Canonical Ownership

| Subject                                          | Canonical public document                        |
| ------------------------------------------------ | ------------------------------------------------ |
| Project overview and authoritative port map      | `README.md`                                      |
| Supported product scope and non-goals            | `docs/product-scope.md`                          |
| Implemented module boundaries and system flow    | `docs/architecture.md`                           |
| Trust-accounting behavior and event workflows    | `docs/accounting.md`                             |
| Significant engineering decisions                | Accepted ADRs in `docs/adr/`                     |
| Development and verification commands            | `docs/runbooks/local-dev.md`                     |
| Contribution requirements and Definition of Done | `CONTRIBUTING.md`                                |
| Released capabilities                            | `CHANGELOG.md`                                   |
| Broad future direction                           | `docs/ROADMAP.md`                                |
| Azure infrastructure operation                   | `infra/README.md` and `docs/runbooks/restore.md` |
| Data-handling and safeguards posture             | `docs/compliance/data-handling.md`               |
| Cross-agent repository rules                     | `AGENTS.md`                                      |

The code and executable configuration remain the runtime truth. When they disagree with a living
document, fix the drift in the same change. Accepted ADRs own the rationale for a decision; changing
one requires a superseding ADR or an explicit amendment consistent with the ADR policy.

Private plans govern unpublished sequencing, commercial decisions, and customer work. They do not
override the public engineering contract after code, tests, and accepted ADRs establish behavior.

## Duplication Rules

- Summaries may repeat stable orientation, but mutable tables, commands, state, and detailed rules
  belong in their canonical document.
- Link to a canonical section instead of copying it when readers can reasonably follow the link.
- README keeps a short quick start and the authoritative port map; the local-development runbook owns
  the complete command reference.
- `AGENTS.md` intentionally repeats critical commands and invariants because coding agents require
  them in immediate context. `CLAUDE.md` is a tool-specific adapter that points to `AGENTS.md`.
- CONTRIBUTING may repeat verification commands as merge requirements, not as an operational command
  reference.
- ADRs and changelog entries may repeat enough context to remain intelligible historical records.

## Lifecycle Metadata

Living documents under `docs/` should declare these fields immediately after the title:

```markdown
- **Audience:** Who uses the document
- **Status:** Living guide | Living policy | Draft | Historical baseline
- **Owner:** Maintainers or the responsible team
- **Last reviewed:** YYYY-MM-DD
```

ADRs retain their existing status/date format. Root community-health files use their conventional
formats and do not need this metadata block.

Review dates are evidence of a real content review, not a timestamp to update mechanically. A draft
must state what blocks acceptance. A historical document must identify the living document or ADR that
supersedes it.

## Link And Path Rules

- Public documentation must not link to `private/`, ignored artifacts, local absolute paths, or
  customer-controlled resources.
- Relative links are preferred for repository content; external links should point to authoritative
  sources.
- Moving or deleting a document requires a repository-wide reference sweep in the same change.
- Maintained public documents participate in link-check CI. Historical private artifacts do not.

## Automated Enforcement

Run the complete local gate from `web/`:

```bash
npm run docs:check
```

The command checks Prettier formatting, Markdown structure, spelling, lifecycle metadata, local and
private-link boundaries, copied mutable commands, obsolete authority claims, and ADR index consistency.
The ordinary CI web job runs the same command on every pull request. The separate Lychee workflow
checks external links and anchors on documentation changes and on its weekly schedule.

Project-specific spelling belongs in `cspell.json`; add stable domain terms, product names, or fixture
names, not accidental misspellings or arbitrary code identifiers. Markdownlint is a structural check;
Prettier remains the formatter.

## Change Checklist

When behavior changes, update only the owning document and affected summaries:

1. Identify the canonical owner from the table above.
2. Update that document with the code or configuration change.
3. Check README, AGENTS, CLAUDE, and the docs index only when their summaries or navigation drift.
4. Add or supersede an ADR when the decision policy requires it.
5. Run `npm run docs:check` from `web/` before opening the PR.
