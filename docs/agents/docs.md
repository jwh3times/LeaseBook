# Docs

## Doc map

Every committed Markdown document at the root, under `docs/`, under `infra/` or in
`.claude/agents/` (except the `docs/adr/ADR-*.md` records) has its own top-level bullet here,
opening with its path in backticks. `scripts/check-docs.mjs` fails `npm run docs:check` when one is
missing: add the bullet in the change that adds the document. Editing a living document under
`docs/` sets its `- **Last reviewed:**` to the day of the commit, except for the documents in
`reviewDateExempt` in `scripts/check-docs-policy.mjs`; the skill settings files in its
`skillSettings` (this one among them)
are not living documents and carry no such line. Work state — what is done, next or blocked — is an
issue, never a doc edit, and no plan file (`private/TODO.md`, `private/roadmap.md` or a successor)
is recreated.

- `AGENTS.md` — the cross-agent engineering contract: repository state, ground rules, commands,
  domain guidance routing, agent skills (development flow, shared skills, trackers, human follow-ups,
  machine handoff), architecture, invariants, working conventions. **When:**
  - a milestone merges or the frontier changes: Repository State, after the merge only;
  - a command, port or script changes (`scripts/dev.ps1`, `web/package.json`, `package.json`, the
    verbs under `src/LeaseBook.Web/Cli/`): Commands;
  - a module, boundary or invariant changes (`src/LeaseBook.Modules.*/`, the RLS helper): Architecture
    and Non-Negotiable Invariants;
  - an agent or skill is added, removed or replaced (`.claude/agents/`, `.agents/skills/`,
    `skills-lock.json`): Domain Guidance Files and Agent skills.
- `CLAUDE.md` — the Claude Code adapter: imports `AGENTS.md` and lists the specialist subagents.
  **When:** the subagent roster or a Claude-only mechanic changes; never repository state.
- `CONTEXT.md` — the domain glossary; a term settles here or nowhere, through `/domain-modeling`.
  `frozen` for this skill: report a term the code contradicts instead of editing it, and never
  rename code to match it.
- `CONTRIBUTING.md` — Definition of Done, local workflow, review expectations, CI gates, versioning
  and releases. **When:** the Definition of Done, a required check, a formatter, the versioning rule
  or the development flow changes.
- `README.md` — public overview, implemented-feature table, quick start, repository layout, and the
  authoritative port map. **When:** a top-level capability merges; a quick-start command or the layout
  changes; a port changes — check the map against `src/LeaseBook.Web/Properties/launchSettings.json`,
  `web/vite.config.ts`, `docker-compose.yml`, the `Dockerfile` and the `infra/` ingress in the same
  change.
- `CHANGELOG.md` — Keep a Changelog; `Unreleased` accumulates and a dated section is cut only on a
  major or minor line. `ship-owned`.
- `VERSION` — the requested release line. `ship-owned`.
- `CODE_OF_CONDUCT.md` — community standards. `owner-only`.
- `LICENSE` — `owner-only`.
- `SECURITY.md` — vulnerability reporting, supported versions, security-critical scope. **When:** the
  reporting route or a scope area's description changes; never finding detail (`Security findings`
  in `issue-tracker.md`).
- `SUPPORT.md` — where to ask for help. **When:** the issue templates or the support route change.
- `docs/README.md` — the documentation index. **When:** a document is added, removed, renamed or
  changes owner.
- `docs/documentation-policy.md` — classification, ownership table, lifecycle metadata, link rules,
  automated enforcement. **When:** a documentation rule or owner changes, or what
  `npm run docs:check` checks (`scripts/check-docs-policy.mjs`, `cspell.json`, `.markdownlint-cli2.jsonc`,
  the `docs:*` scripts in `web/package.json`).
- `docs/product-scope.md` — the public product boundary and non-goals. `owner-only`: a change is a
  formal scope decision.
- `docs/ROADMAP.md` — public shipped state and broad direction. **When:** a milestone or roadmap
  item merges, or the direction changes; never from an unmerged branch.
- `docs/architecture.md` — the public map of the system as implemented. **When:** a module, port,
  adapter, cross-module boundary, background job or runtime shape changes
  (`src/LeaseBook.Modules.*/`, `src/LeaseBook.Web/`, `infra/`).
- `docs/blueprint.md` — the historical pre-M0 baseline that accepted ADRs supersede. `frozen`.
- `docs/accounting.md` — double-entry model, dual basis, trust equation, business-event catalog.
  **When:** a business event or posting template is added or changed
  (`src/LeaseBook.Modules.Accounting/Features/Posting/`), or an account class changes.
- `docs/perf.md` — read-path p95 method and recorded numbers. **When:** `perf-probe`, the load
  fixture or a measured read path changes; numbers only from a new measured run.
- `docs/testing/coverage.md` — coverage method, commands, dated baseline. **When:**
  `tests/coverage.settings.xml`, `scripts/coverage-summary.mjs`, `scripts/e2e-coverage.mjs`,
  `scripts/route-inventory.mjs` or `.github/workflows/coverage.yml` changes.
- `docs/testing/scenarios.md` — the scenario catalogue, its two checks, and a generated table.
  **When:** `scripts/scenario-catalogue.mjs` or a scenario changes. The table between the
  `scenario-catalogue` markers is regenerated with `node scripts/scenario-catalogue.mjs doc`, never
  edited by hand.
- `docs/testing/mutation.md` — mutation method, commands, equivalents list, gate, run times.
  **When:** `scripts/mutation.mjs`, a module's gate or `.github/workflows/mutation.yml` changes.
- `docs/payments/simulated-payment-spec.md` — the non-live payment lifecycle and its acceptance
  matrix. **When:** simulated-payment behavior changes (`src/LeaseBook.Modules.Payments/`).
- `docs/payments/fee-and-settlement-spec.md` — processor fees, batched payouts, funds in transit.
  **When:** fee or payout behavior changes (`src/LeaseBook.Modules.Payments/`).
- `docs/adr/README.md` — the ADR index: one row per `docs/adr/ADR-*.md` (number, title, status with
  its amenders, date), reconciled whole on every audit; `npm run docs:check` enforces it. **When:**
  an ADR is added or its status changes.
- `docs/adr/template.md` — the ADR format. `owner-only`.
- `docs/adr/ADR-*.md` — decision records. `frozen`: report a contradiction with the code instead
  of editing either; a changed decision is a new ADR or the author's addendum. Report when a change
  needs a new one: a library added, removed or replaced (`Directory.Packages.props`,
  `web/package.json`), a module-boundary exception, a new scheduler, an invariant relaxed or added,
  or a departure from a `docs/blueprint.md` default.
- `docs/runbooks/local-dev.md` — the complete command reference: dev commands, seed workflow,
  common operations. **When:** a `scripts/dev.ps1` or `scripts/dev.sh` command, a seed target, a CLI
  verb, a migration step or a port changes.
- `docs/runbooks/payment-simulation.md` — the payment fixture, signed driver, restart and reset.
  **When:** `scripts/payment-fixture.ps1`, a fixture manifest or a simulated-payment command
  changes.
- `docs/runbooks/stripe-sandbox-probe.md` — the maintainer's Stripe sandbox probe. **When:**
  `scripts/stripe-probe.mjs` or `scripts/stripe-sandbox-run.mjs` changes.
- `docs/runbooks/blank-stock-checks.md` — blank-stock refund checks, from the bank's spec sheet to
  approval. **When:** the blank-stock face, the MICR line's layout or offsets, the MICR details
  form, or a refusal code on the print routes changes.
- `docs/runbooks/restore.md` — disaster recovery and PITR. **When:** backup or restore tooling, or
  the database in `infra/`, changes.
- `docs/runbooks/diagnostics.md` — error contract, event-id catalog, triage. **When:** an error
  code, a logged event id or the bulk-run diagnostics change.
- `docs/migration/parallel-run.md` — the public parallel-run checklist. **When:** the import
  toolkit or its sign-off gate changes (`src/LeaseBook.Migrator/`).
- `docs/compliance/data-handling.md` — GLBA and PII handling. **When:** data handling or a
  safeguard changes. Its `Last reviewed` tracks the external review: never bump it.
- `docs/compliance/privacy-notice-draft.md` — the privacy notice skeleton. **When:** what the
  product collects or shares changes; the same review-date rule.
- `docs/research/azure-monitor-opentelemetry-distro.md` — dated vendor research. `amend-only`.
- `docs/research/micr-e13b-refund-checks.md` — dated MICR and bank-acceptance evidence.
  `amend-only`.
- `docs/research/stripe-connect-payment-lifecycle.md` — dated provider evidence for the payment
  design. `amend-only`.
- `docs/agents/dev-flow.md` — the development-flow gate, what counts as code, routing, exemptions,
  branch and PR linking. **When:** any of those changes; `Issue link` in `ship.md` with it.
- `docs/agents/domain.md` — the domain-docs layout. **When:** the glossary or ADR layout changes.
- `docs/agents/issue-tracker.md` — tracker conventions, which tracker, the one rule, wayfinding
  operations, and its settings. **When:** a convention, the board or a board field changes.
- `docs/agents/triage-labels.md` — the triage labels, plus `blocked-by-human` and `operator`.
  **When:** a label is added or its meaning changes.
- `docs/agents/human-follow-up.md` — follow-up settings for the shared skills. **When:** the
  follow-up destination, labels, board fields or wiki change.
- `docs/agents/workspace.md` — workspace settings. **When:** the companion, a sync or dev-stack
  command, or an ignored path changes (`.gitignore`, `scripts/dev.ps1`, `scripts/sync-main.mjs`).
- `docs/agents/releases.md` — release settings. **When:** `.github/workflows/version.yml`,
  `.github/workflows/changelog.yml` or the cut policy changes.
- `docs/agents/ship.md` — fast checks and the issue link. **When:** a required check or a CI step it
  mirrors changes (`.github/workflows/ci.yml`, the ruleset), or the linking rule changes.
- `docs/agents/handoffs.md` — handoff settings. **When:** machine-local state a session depends on
  changes.
- `docs/agents/docs.md` — this file. **When:** a document is added, removed or changes owner.
- `infra/README.md` — Azure topology, the environment-variable contract, the first-apply table.
  **When:** `infra/**/*.bicep`, a parameter file or a deploy workflow changes.
- `infra/db/azure-bootstrap.md` — Postgres role bootstrap against a live server. **When:**
  `infra/db/bootstrap.sql` or the role model changes.
- `web/ICONS.md` — the icon pack in `web/public/`: colors, files, the two crops and the manifest's
  background. **When:** an icon file, a crop or `site.webmanifest` changes.
- `tests/LeaseBook.Tests.Web/Fixtures/MicrE13BReference/*.md` — the provenance and licence of the
  third-party E-13B outlines `E13BGlyphTests` checks against. `frozen`.
- `.claude/agents/dotnet-api.md` — backend patterns, CQRS slices, module boundaries. **When:** the
  endpoint or slice pattern, or a boundary rule, changes. Mirrored to `.codex/agents/`, like every
  agent file.
- `.claude/agents/react-frontend.md` — the SPA design system and query hooks. **When:** a
  primitive, token or data-access pattern changes.
- `.claude/agents/postgres-specialist.md` — RLS, migrations, DDL conventions, the three roles.
  **When:** the RLS helper, a role or a migration convention changes.
- `.claude/agents/trust-accounting.md` — posting templates, dual basis, the trust equation.
  **When:** a posting rule changes.
- `.claude/agents/code-reviewer.md` — the pre-merge invariant review. **When:** an invariant it
  checks changes.
- `.claude/agents/azure-infrastructure.md` — Bicep, deploy workflows, Key Vault, PITR. **When:**
  `infra/` or a deploy workflow changes.
- `.agents/skills/*/` — installed skills, mirrored to `.claude/skills/`: the `mattpocock/skills`
  set in `skills-lock.json`, and `end-session`, `handoff`, `lets-go`, `ship` and `docs-updater`
  from `jwh3times/skills`. `frozen`: fixed upstream, never here. **When:** a skill is added, removed
  or replaced: `AGENTS.md` ("Domain Guidance Files", "Agent skills") and `CLAUDE.md`.
- `.github/pull_request_template.md` — the PR body: the link-line slot and the Definition of Done
  checklist. **When:** the linking rule or the Definition of Done changes.
- `private/README.md` — the companion's index and operating guide: document authority, bootstrap,
  two-repository workflow, credentials and backup. **When:** a private document is added, moved or
  retired, or the bootstrap or backup procedure changes.
- `private/planning/m{N}_plan.md`, `private/planning/m{N}_retro.md` — milestone plans and
  retrospectives, the record of how completed work went. **When:** a milestone closes (its retro,
  written then, never mid-milestone); earlier ones are `amend-only`.
- `private/planning/design-history/` — historical design specs and plans. `frozen`.
- `private/planning/private-workspace-portability-plan.md` — historical. `frozen`.
- `private/security-review-findings.md` — the earlier review passes (F1–F11). `append-only`: a
  review write-up only; a new finding is a draft repository advisory.
- `private/architecture-review-findings.md` — architecture reviews and their dispositions.
  `append-only`, as above.
- `private/appfolio.md` — the AppFolio export catalog. **When:** an import definition or its
  validation gate changes (`src/LeaseBook.Migrator/`).
- `private/research/*.md` — dated research. `amend-only`.
- `private/LeaseBook_PRD_v1.0.md` — product requirements and scope authority. `owner-only`.
- `private/claude_design_files/` — the interactive prototype and design source. `owner-only`.

**Never in public docs:** pricing, strategy, customer identity and internal analysis; compliance
and legal engagements; an unpatched weakness or exploit detail; the companion's locator, its issue
numbers, and its issue and wiki links; private infrastructure values and credential values; PRD,
milestone-plan, retrospective and prototype content.

## Drift probes

- Ports: the README's "Port map" against `src/LeaseBook.Web/Properties/launchSettings.json`,
  `server.port` and the `/api` proxy target in `web/vite.config.ts`, `ports:` in
  `docker-compose.yml`, `EXPOSE` in the `Dockerfile`, `targetPort` in `infra/**/*.bicep`
- Modules: the `src/LeaseBook.Modules.*/` directories and `src/LeaseBook.Migrator/`
- Business events: `src/LeaseBook.Modules.Accounting/Features/Posting/Events/BusinessEvents.cs`
- Endpoints: `IEndpointModule` implementations under `src/**/Endpoints/`
- CLI verbs: `src/LeaseBook.Web/Cli/`
- Migrations: `src/LeaseBook.Web/Migrations/*.cs`
- Dev commands: the `ValidateSet` in `scripts/dev.ps1` and the `case` in `scripts/dev.sh`
- npm scripts: `scripts` in `web/package.json` and `package.json`
- Dependencies (ADR coverage): `Directory.Packages.props`; `dependencies` in `web/package.json`
- Toolchain pins: `global.json`, `.nvmrc`, `web/.nvmrc`, `engines` in `package.json`
- ADR index: the header block of each `docs/adr/ADR-*.md` (`npm run docs:check` reconciles it)
- Scenario table: `node scripts/scenario-catalogue.mjs check`
- CI jobs: the keys under `jobs:` in `.github/workflows/*.yml`, then each job's `name:` — never a
  bare `name:` grep, which counts steps too
- Required status checks: `gh api repos/jwh3times/LeaseBook/rules/branches/main`
- Agents and skills: `.claude/agents/*.md`, `.agents/skills/*/`, `skills-lock.json`

## Docs validation

- `web/`: `npm run docs:check` — Prettier, markdownlint and cspell, the docs-policy tests, then
  `scripts/check-docs-policy.mjs` (lifecycle metadata and review dates, copied commands, authority
  claims, the ADR index) and `scripts/check-docs.mjs` (local links and anchors, private links,
  `Doc map` coverage)
- repo root, when `private/.git` exists: `node private/scripts/check-docs.mjs` — the companion's own
  gate
