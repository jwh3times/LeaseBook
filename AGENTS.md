# AGENTS.md

This is the canonical cross-agent engineering contract for the repository. Tool-specific instruction
files may add integration details but must defer to this file for project rules, commands, invariants,
and documentation ownership.

## Repository State

LeaseBook is a property-management SaaS for small residential property managers, differentiated on
correct trust accounting and low click-depth UX. Milestones M0-M8 implement PRD Phase 1; each ends
in a demonstrable state. Per-milestone plans and retrospectives live in `private/planning/` as
`m{N}_plan.md` and `m{N}_retro.md`.

**Open work lives in GitHub Issues on the LeaseBook project board (project 3), across the public and
private trackers — that is the live source of truth for what remains.** `private/planning/*_retro.md`
remains the source of truth for how completed work actually went. Do not trust summaries, including
this one, for current progress.

- M0-M7 are complete and merged to `main`: foundations; the trust-accounting engine; Directory; the
  tenant ledger action hub; Banking and Reconciliation; Owner Statements and Reporting; Bulk
  Operations; and the Migration toolkit and import-first onboarding.
- M8, Hardening, Compliance and Beta Launch, remains the Phase 1 frontier. What has shipped is in
  `CHANGELOG.md` and `docs/adr/`; what remains is summarized in `docs/ROADMAP.md` and sequenced on
  the board's `Track`/`Gate` and `Priority`/`Rank` fields (`docs/agents/issue-tracker.md`).
- Operator-gated work is deferred and is not ordinary engineering work: Azure OIDC federation, ACR,
  enabling the `deploy-dev`/`deploy-prod` workflows, live Key Vault and managed identity, the first
  PITR drill, deployment-dependent telemetry and alerting, and the ADR-041 first-apply steps.
- Phase 2 (tenant and owner portals, simulated payments, payouts, refund checks, the Stripe sandbox
  adapter; ADR-003, -046, -048, -050 to -054) runs in development fixtures only — there is no live
  payment provider. Before running or resetting payment fixtures, read
  `docs/runbooks/payment-simulation.md`; the dedicated database and generation binding protect
  ordinary organizations and golden data.

The `private/` directory is gitignored by the public repository and is a separately versioned
confidential checkout for authorized maintainers. Its repository locator and bootstrap details live
outside the public repository, and it may be absent in a public clone. It holds:

- `private/planning/m{N}_retro.md`: per-milestone retrospectives — the durable record of how
  completed work actually went
- `private/security-review-findings.md`, `private/architecture-review-findings.md`: historical
  records of the review passes; new findings are draft repository advisories, not appends
- `private/appfolio.md`: AppFolio export catalog (reference data)
- `private/LeaseBook_PRD_v1.0.md`: product requirements and scope authority
- `private/claude_design_files/`: interactive UI prototype, design-system source of truth, and
  design/product analysis

Anything confidential, including pricing, strategy, customer identity, and internal analysis, belongs
in `private/` and must never be added to the public repository. Commit and push public and private
changes independently. If `private/` is absent and the task depends on it, ask the user for the
relevant PRD or design context before starting milestone work.

## Ground Rules

Verify, do not assume.

- Do not build on unverified assumptions. When a task depends on a fact that cannot be confirmed from
  code, docs, or a quick local check, stop and verify before designing against it.
- This is especially important for trust-accounting and domain facts: NCREC trust-money rules
  (21 NCAC 58A .0116-.0118; record retention is .0108), basis behavior, posting-template lines, prototype golden figures, demo-seed
  numbers, EF/RLS/posting runtime behavior, and anything that can affect fiduciary correctness.
- Ground truth is usually obtainable locally:
  - Scope and intent: `private/LeaseBook_PRD_v1.0.md` and `private/claude_design_files/`
  - Built or decided work: closed issues and `private/planning/*_retro.md`
  - Real figures and behavior: run `check-invariants`, golden/invariant/property suites, query the
    seeded demo org, or read the migration, RLS-helper, and posting-template code
- Designing a structure to discover an unknown at runtime is still building on an assumption. Verify
  the discovery against real data such as journal replay or the golden dataset.
- A sensible default for a genuinely low-stakes choice is fine. State the assumption and proceed.
  If being wrong would force rework, move a golden figure, or ship fiduciary incorrectness, verify or
  ask.

## Commands

Solution: `LeaseBook.slnx`. The SPA lives in `web/`. The project expects .NET 10 SDK, Node 26, and
Docker for local Postgres and integration tests.

Every bound port is mapped in the Port map section of the root `README.md`: inner-loop dev
`5373`/`5080`/`5632`, full Docker stack host port `8082`, container and production ingress `8080`,
and `LEASEBOOK_APP_PORT`/`LEASEBOOK_DB_PORT`/`LEASEBOOK_PGADMIN_PORT` overrides. Keep that table and
the configs it cites in sync when a port changes.

### Backend (.NET, run from repo root)

- Build: `dotnet build LeaseBook.slnx -c Debug`
- All tests: `dotnet test --solution LeaseBook.slnx`
- One project: `dotnet test --project tests/LeaseBook.Tests.Integration/LeaseBook.Tests.Integration.csproj`
- One test class, xUnit v3/MTP: `dotnet test --project <proj> --filter-class '*OrgIsolationTests'`
- One test method, xUnit v3/MTP: `dotnet test --project <proj> --filter-method '*Reads_under_an_org_context_see_only_that_orgs_rows'`
- Format check: `dotnet format --verify-no-changes --exclude src/LeaseBook.Web/Migrations`
- Run API on `:5080`: `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web`
- Integration tests use Testcontainers. Docker must be running; local compose is not required.
- Coverage, mutation testing (every module's gate is on), the scenario catalogue, `perf-probe`, the
  `capabilities` CLI verb (the only capability write surface; ADR-028) and a maintainer's Stripe
  sandbox run each have an owning doc with the exact commands: `docs/testing/coverage.md`,
  `docs/testing/mutation.md`, `docs/testing/scenarios.md`, `docs/perf.md`,
  `docs/runbooks/local-dev.md`, `docs/runbooks/payment-simulation.md`. Read it before running one.

### Database (run from repo root)

Run `dotnet tool restore` once for `dotnet-ef`.

- Local Postgres: `./scripts/dev.ps1 up`, `down`, `reset-db`, or `psql`
- Full stack in Docker: `./scripts/dev.ps1 app-up`, `app-down`, or `app-logs`
- Add migration: `dotnet ef migrations add <Name> --project src/LeaseBook.Web --context AppDbContext`
- Apply migrations as migrator role: `dotnet ef database update --project src/LeaseBook.Web --context AppDbContext`
- Seed demo org: `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- seed --org demo`
- Check invariants: `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- check-invariants --org demo`
- Check all org invariants: `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- check-invariants --all`
- Seed the 300-unit load fixture: `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- seed --org load`
- Seed the all-scenario org (every template/workflow/report, golden-locked): `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --project src/LeaseBook.Web -- seed --org scenario`

### Web (run from `web/`)

Scripts are in `web/package.json` (`dev` on `:5373` proxies `/api` to `:5080`; `lint`, `typecheck`,
`test`, `build`). The ones with traps:

- Regenerate API client (emits the contract itself; no running host or database): `npm run api:generate`
- e2e, against a seeded host: `npm run e2e`
- e2e on a fresh copy of a seeded template database, leaving the dev database alone (stop a running
  host first; the suite changes the data it runs on, so a second plain run fails): `npm run e2e:fresh`
- e2e with host and browser coverage (stop a running host first): `npm run e2e:coverage`. It also
  checks each scenario against what its tests did and runs `check-invariants --all`, and fails on
  either (ADR-056)
- Public documentation quality and policy gate: `npm run docs:check`

### Container

- Build image: `docker build -t leasebook .`
- Run with `ConnectionStrings__Default` set. The container serves SPA and `/api` on internal port
  `8080`.
- Run the whole product locally, including db, migrate, seed, and app:
  `./scripts/dev.ps1 app-up`. Compose `full` profile serves at `http://localhost:8082`.
- Migrations run as the migrator role through a one-shot `migrator` target image, never at app
  startup.

## Domain Guidance Files

Specialist-agent guidance is authored under `.claude/agents/`, mirrored to `.codex/agents/*.toml` for
Codex. Skills run the opposite direction: **`.agents/skills/` is authored** — that's where the skills
installer writes when installing or updating a skill, so keeping it the source means installing stays
a one-way operation with no manual copying — and `.claude/skills/` is generated for Claude Code, with
each generated `SKILL.md` carrying a `# GENERATED — do not edit` banner as a YAML comment on line 2
(the frontmatter's opening `---` stays on line 1). Whichever harness you are in, these are project
knowledge — before editing one of these domains, read the corresponding file and apply its rules
unless a higher-level invariant in this file conflicts.

**Edit `.claude/agents/` and `.agents/skills/` only** — never `.codex/agents/` or `.claude/skills/`
directly; both are generated and hand-editing either is overwritten on the next sync. After changing
an agent or a skill, run `node scripts/sync-agents.mjs` (or `npm run sync:agents`) and commit the
result. CI runs the script's `node:test` suite and regenerates with `--check`, failing the build if a
committed mirror is stale, and `npm run lint:agents` (from the root) rejects sources that read
wrongly from their mirrored copies: a bare `$0`–`$9` in a `SKILL.md`, a relative link that breaks
under `.claude/skills/`. Run `npm run docs:format` (from `web/`) **before** the sync, not
after — formatting the generated copy instead of the authored source just re-drifts it on the next
pass. Never reintroduce symlinks under `.claude/skills/` (the sync script reports and removes them): this
repo's `core.symlinks` is `false`, so git would silently duplicate every file inside a symlinked
directory into the repository instead of recording a link (verify with `git config core.symlinks` and
`git add -n <path>` — a multi-file listing instead of one entry means this has happened).

| Work type                                                               | Read first                               |
| ----------------------------------------------------------------------- | ---------------------------------------- |
| .NET features, endpoints, commands/queries, integration tests           | `.claude/agents/dotnet-api.md`           |
| React components, hooks, design tokens, frontend tests                  | `.claude/agents/react-frontend.md`       |
| Migrations, RLS policies, DB schema design, Postgres queries            | `.claude/agents/postgres-specialist.md`  |
| Accounting posting logic, journal entries, trust equation changes       | `.claude/agents/trust-accounting.md`     |
| Reviewing a diff for correctness bugs before merging                    | `.claude/agents/code-reviewer.md`        |
| Azure infra, Bicep, deploy workflows, Key Vault, managed identity, PITR | `.claude/agents/azure-infrastructure.md` |

Cross-cutting rules, module boundaries, organization-isolation model, and trust-accounting invariants in this file
apply to all work. If a domain guidance file conflicts with these invariants, the invariant wins.

Documentation drift is the `docs-updater` skill's job (see "Shared skills" under "Agent skills"),
not a guidance file's. The `/ship` skill runs it scoped to the branch's diff, and warns when a PR
opens with no issue link, or links one that is missing from the project board or on it with
`Track`/`Gate` unset. Both are session-scoped — no harness is guaranteed to run them, and CI does
not. When source changes affect docs, ports, ADR-worthy decisions, user workflows, commands, or
business events, check documentation drift and update the relevant docs in the same change.

## Agent skills

### Development flow

<!-- shared:development-flow -->

The flow skills are this repository's binding process for changing code. Read
`.agents/skills/ship/settings/dev-flow.md` (the routing, exemptions and issue linking) and
`docs/agents/dev-flow.md` (what this repository adds) before the first edit of a change, and whenever
no ready issue exists. `/ask-matt` maps the flow itself.

- **The gate.** Every code edit starts from a ready issue: an open `ready-for-agent` issue whose body
  is a `/to-spec` spec or a `/to-tickets` ticket or that carries a `/triage` brief, or a `bug` issue
  worked through `/diagnosing-bugs`, which needs the number and no spec. With none, stop before the
  first edit and route the user. Reading code, running tests and answering questions need no issue.
- **Private work.** A ready issue in the private tracker counts. Its number stays out of the branch,
  the PR, commits and public issues, and it is closed by hand after the merge.
- **The human starts every phase.** `/wayfinder`, `/grill-with-docs`, `/to-spec`, `/to-tickets`,
  `/implement` and `/triage` stay user-invoked. At a phase boundary, name the next command, say in
  one line why, and wait. Inside a phase, use `/tdd`, `/code-review`, `/diagnosing-bugs`,
  `/domain-modeling`, `/codebase-design`, `/prototype` and `/research` whenever they fit.
- **Exempt from the gate:** dependency bumps, changes touching only docs and agent config, release
  prep, and an explicit user waiver recorded in the PR body as `Flow waived by the user: <reason>`.
- **Linking.** Branches are `<type>/<issue#>-<slug>`; the PR body opens with `Closes #N` or
  `Part of #N`.
- **The flow ends at `/ship`.** `/implement` stops at a commit; `/ship` refreshes the docs, writes the
  changelog and the PR body, and opens the PR. `/ask-matt` also routes to `/pr`, `/retro` and
  `/implement-spec`, which are not installed here.
- **`/handoff` is the shared skill.** It parks the session for another machine or harness, closes it
  out with `/end-session`, and `/lets-go` resumes it. It does not write the portable file `/ask-matt`
  describes, so a `/prototype` detour stays in the session or uses a branch.
- **A wizard is not the record.** A step only a human can take follows the `## Procedure` in
  `.agents/skills/ship/settings/human-follow-up.md`, whose instructions stand without the script.
- **Research goes where it is safe to keep.** `/research` findings that would expose private material
  go to the private companion, where there is one, never to an ignored folder with no repository
  behind it. Details of an unpublished vulnerability are a draft advisory, never a research file.

<!-- /shared:development-flow -->

### Shared skills

<!-- shared:shared-skills -->

`end-session`, `handoff`, `lets-go`, `ship`, `docs-updater` and `what-next` install from
`jwh3times/skills`, and are never edited here: reinstalling overwrites a local edit. A fix goes
upstream, and what they need to know about this repository goes in the settings files under
`docs/agents/`, which each skill proposes on first run.

The other shared skills come from one `mattpocock/skills` snapshot (v1.3.2, pinned by commit), the
same in every repository, and are never edited here either: what they need to know goes in this
file or `docs/agents/`. Moving to a newer snapshot is a change in `jwh3times/skills`. Keep
`.agents/skills/` out of every formatter: `scripts/check-skills.mjs` fails when an installed copy
differs from `skills-lock.json`, or the lock from these commands.

Re-running the install is the update; never run `npx skills update`, which leaves `.claude/skills/`
linked to its source instead of copied:

```bash
npx skills add jwh3times/skills -s end-session -s handoff -s lets-go -s ship -s docs-updater -s what-next -a codex --copy -y
npx skills add mattpocock/skills#d1478fcb140db0b93c0c8df17bcfd311844749d0 \
  -s ask-matt -s code-review -s codebase-design -s diagnosing-bugs -s domain-modeling -s grill-me \
  -s grill-with-docs -s grilling -s implement -s improve-codebase-architecture -s prototype \
  -s research -s setup-matt-pocock-skills -s tdd -s teach -s to-questionnaire -s to-spec \
  -s to-tickets -s triage -s wait-what -s wayfinder -s wizard -s writing-for-agents \
  -a codex --copy -y
node scripts/sync-agents.mjs
```

`scripts/sync-agents.mjs`, `lint-agent-neutrality.mjs`, `check-docs.mjs`, `check-skills.mjs`, and
`sync-main.mjs`, `next-version.mjs` and `check-changelog.mjs` where this repository carries them, are
canonical in `jwh3times/skills` with their tests: fix them there, then copy them here.
`sync-agents.mjs` also writes the marked blocks in this file: change them in `jwh3times/skills`, not
here.

<!-- /shared:shared-skills -->

The `Doc map` in `docs/agents/docs.md` lists every canonical document, and `npm run docs:check`
requires it.

### Issue tracker

Route engineering work by **whether the text is safe in public git history**; required human
follow-ups always use the private tracker.

- **Public** — `jwh3times/LeaseBook`. Anything a public PR closes: engineering work, bugs,
  architecture questions, deployment steps. This is the default.
- **Private** — the private companion repository, whose locator is the `private/` checkout's own
  git remote and stays out of this tree. Anything confidential: an accepted security risk,
  compliance and legal engagements, customer identity, pricing and strategy (an unpatched weakness
  is a draft repository advisory instead). Also all required human follow-ups arising from
  agent-completed work, even when the engineering change is public. These close on events in the
  world, not on merged PRs.

Never reference a private issue from a public PR, commit, or issue — the reference itself leaks its
existence. Both trackers share one board (project 3).

**One rule prevents the drift this replaced:** an issue states its own question and never restates
another issue's status. Use task lists and `Part of #N` for relationships — GitHub renders child
state live, so a parent stays current with nobody maintaining it.

See [`docs/agents/issue-tracker.md`](docs/agents/issue-tracker.md) and `gh` CLI usage there.

Before reporting agent work complete, execute the required human-action handoff
([`docs/agents/human-follow-up.md`](docs/agents/human-follow-up.md)): create
or update private follow-up issues, add them to project 3 with labels, Track/Gate and Priority/Rank, and publish
linked step-by-step private wiki procedures indexed by `human-todo`. Verify publication and report
both links. If access prevents publication, report the handoff as incomplete with its blocker.

**Deployment exception:** LeaseBook is not publicly deployed. Consolidate all deployment-related
authoring and operator follow-ups in the single private public-distribution issue and its linked
wiki page, as `docs/agents/human-follow-up.md` specifies. Add to those artifacts instead of
creating separate deployment board items or wiki pages. Defer execution until public distribution is
elected; close the consolidated item only on distribution and completed deployment acceptance.

### Machine handoff

Switching machines uses `/handoff`, then `/lets-go` on the other machine — the machine handoff, not
the portable-file handoff the vendored `ask-matt` skill describes. How the handoff folder reaches a
machine is that machine's config (`.agents/skills/handoff/machine-config.md`); what a LeaseBook
handoff carries: `docs/agents/handoffs.md`.

### Triage labels

Default label vocabulary: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`,
`wontfix`. See [`docs/agents/triage-labels.md`](docs/agents/triage-labels.md).

### Domain docs

Single-context layout — `CONTEXT.md` and `docs/adr/` at the repo root. See
[`docs/agents/domain.md`](docs/agents/domain.md).

### MCP servers

The shared servers are in `.mcp.json`, which Claude Code asks you to approve on first use.
`.codex/config.toml` is generated from it, and Codex loads it once you trust the project in your own
Codex config. Edit `.mcp.json` only, then run `node scripts/sync-mcp.mjs`; CI fails a stale copy.
Entries carry no `env` or `headers`, so a server that needs a key stays in your own config. Nothing
updates the pinned `@playwright/mcp` version: bump it by hand.

- `microsoft-learn` for .NET, ASP.NET Core, EF Core and Azure; `context7` for the SPA and
  third-party libraries. Query one before designing against an API from memory. Both send the
  query text to an outside service, so keep `private/` material and confidential detail out of it.
- `playwright` drives a browser against the running app (`:5373`) to check a change or a UX click
  budget. It does not replace e2e coverage of a budgeted flow.
- GitHub work goes through `gh`, not an MCP server.
- There is deliberately no database server: a session missing `app.org_id` or `app.persona` reads
  zero rows and reports them as fact. Use `./scripts/dev.ps1 psql` and set both.

## Architecture

`docs/architecture.md` is the canonical public map of the implemented system. `docs/blueprint.md` is
the historical pre-M0 baseline; accepted ADRs and implemented behavior supersede it where the system
evolved. The patterns below are established and test-enforced in built modules and bind future modules.

- Modular monolith: ASP.NET Core host plus one project per module: `Accounting`, `Directory`,
  `Banking`, `Reporting`, `Operations`, `Capabilities`, `Payments`, `SharedKernel`, and `Migrator`.
  Modules reference `SharedKernel` only, enforced by `ModuleBoundaryTests`.
- Cross-module calls go through `Contracts` ports so modules stay extractable. A feature module must
  never read another module's tables or data directly: no cross-module SQL, no cross-module LINQ, and
  no referencing another module's entity types.
- Cross-module reads use a consumer-owned port: an interface declared in the consuming module's
  `Contracts`, implemented by a thin host adapter that delegates to the producing module via
  `ISender`.
- Ports expose batch reads that return maps, never per-id reads, except a posting's read of the one
  tenant or property it names (ADR-014 addendum of 2026-11-14). Adapters run on the ambient RLS
  transaction and must not open a new connection.
- `SharedKernel` stays pure cross-cutting primitives. Module contracts do not live there.
- Within a module, `db.Database.SqlQuery<T>` is acceptable for that module's own analytical reads.
  Prefer EF LINQ for simple reads; reserve raw SQL for window functions, `FILTER`, trigram queries, or
  other queries LINQ cannot express.
- The dedicated reporting/read layer is the sole intentional exception to the no-cross-module-read
  rule. It may read across schemas on purpose and records its own ADR.
- Application pattern: CQRS with vertical slices. One file per feature contains the command/query
  record, `AbstractValidator`, and handler. Minimal APIs only through `IEndpointModule` and
  `TypedResults`; hand-rolled `ISender` dispatcher; no MediatR and no AutoMapper.
- Accounting is the core module and gets the highest test rigor. It uses a double-entry journal:
  `journal_entries` plus `journal_lines`, written only through posting templates keyed to business
  events such as `RentCharged`, `DepositApplied`, and `OwnerDisbursed`.
- Dual-basis posting: lines are tagged cash/accrual/both, so each accounting basis is a query, not a
  transformation.
- Tenant ledgers, owner ledgers, bank registers, and statements are read-model projections of the
  journal, never independently maintained state.
- Stack is PRD-locked: .NET current LTS, React and TypeScript with Vite, PostgreSQL, Azure Container
  Apps in East US 2, Key Vault and managed identity. Defaults such as EF Core, Bicep, QuestPDF, and
  TanStack Query are tabled in `docs/blueprint.md`. Changing one requires an ADR.

## Non-Negotiable Invariants

Violating these is a correctness bug, not a style issue.

### Trust Accounting

- PM income is isolated from owner income at the data-model level by account class, never by a
  reporting filter. No `PMIncome` line may be reachable by any owner-statement query.
- Security deposits and prepayments are liabilities until applied. Income recognition fires only on
  application, with identical behavior in cash and accrual modes.
- Ledgers are append-only. Corrections are linked reversal entries; posted rows are never updated or
  deleted. The runtime DB role has no `UPDATE` or `DELETE` grant on journal/audit tables.
- Every journal entry balances, with debits equal to credits, per basis.
- The trust equation must hold for every trust account at all times:
  bank book balance = owner equity + deposit liabilities + held PM fees.
- Money is `decimal` in C# and `NUMERIC(14,2)` in Postgres. Never use `float` or `double` for money.
- Reconciliation finalize locks the accounting period. Postings into locked periods are rejected.
- Capabilities gate **reachability only** (ADR-028). A capability may decide whether a posting path
  runs at all; it may never change the lines or amounts a business event produces. No capability-derived
  value may become an argument to an Accounting command, business event, or posting-template input.
  Money-affecting parameters belong in `OrgSettings`. A multi-step run resolves the capability set
  once and passes it explicitly; it never re-asks mid-run.

### Organization Isolation

- Postgres RLS is the security boundary. EF global query filters are ergonomics only.
- Organization context is set with `SET LOCAL app.org_id` inside the transaction. Never use session-level
  `SET`, because pooled connections would leak context.
- `OrgScopedExecutor` is the only production setter of the org context (ADR-048). In one
  transaction-local `set_config` statement it sets `app.org_id`, `app.persona`
  (`system`/`staff`/`tenant`/`owner`/`none`) and `app.user_id`. `PersonaResolver` maps the principal's
  roles, and a mixed or unrecognised role set resolves to `none`. `OrgContextCallSiteTests` fails the
  build on a second setter.
- Missing organization context fails closed. So does a missing persona: every session that reads org
  data, including an operator's `psql` or pgAdmin session as `leasebook_app` or `leasebook_ops`, must
  set `app.persona` (for example `staff`) as well as `app.org_id`, or it sees zero rows.
- There are three DB roles:
  - `leasebook_migrator`: schema owner of `public`, migrations only
  - `leasebook_app`: runtime, RLS-subject via `FORCE ROW LEVEL SECURITY`
  - `leasebook_ops`: read-only
- One deliberate exception to migrator ownership: the `hangfire` schema is owned by `leasebook_app`,
  because Hangfire installs and upgrades its own objects and Postgres allows that only to the owner
  (ADR-001). Pre-created by `infra/db/bootstrap.sql`; it is the runtime role's only `CREATE`
  privilege anywhere, and it has none on the database or on `public`. Do not "correct" it to
  migrator ownership — that breaks the first Hangfire version bump. Nothing org-scoped lives there.
- Every new org-scoped table goes through the migrations RLS helper: column, `USING`/`WITH CHECK`
  policy, and `FORCE ROW LEVEL SECURITY` in one call. The helper also applies the deny-by-default
  persona gate (ADR-048): three `RESTRICTIVE` policies (`{t}_persona`, `{t}_persona_no_update`,
  `{t}_persona_no_delete`) that admit `staff` and `system` organization-wide and a portal persona only
  through a grant written explicitly with `Rls.ApplyPersonaGate`. A new table is closed to tenants
  and owners until a migration grants it. Grants derive the caller's owner or tenant from its active
  `owner_access`/`resident_access` link, never from an id the application sets. The ADR-003
  application-layer checks remain the first layer.
- A schema guard test fails CI if any `org_id` table lacks its policy or persona gate, and it pins
  every persona-gate predicate exactly.
- `FORCE ROW LEVEL SECURITY` binds the migrator too, so a migration that rewrites data must bracket
  its DML with `ALTER TABLE <t> NO FORCE ROW LEVEL SECURITY` / `... FORCE ROW LEVEL SECURITY` in the
  same `Sql` block — for every forced table the statement names, not just the target. Without the
  lift the write silently matches zero rows; without the restore the table stays unprotected. EF's
  `InsertData`/`UpdateData`/`DeleteData` builders are not usable for this, since no block can bracket
  them. A source-scanning architecture test fails CI on all three.
- One deliberate exception to org-only policies: the four platform capability tables are gated on
  `SET LOCAL app.platform` (ADR-028), in three different shapes. `entitlements` and
  `capability_cohorts` keep `org_id` and an org read policy **plus** a platform escape — an escape
  rather than no RLS, because forgetting it yields zero rows instead of every org's rows.
  `feature_flags` is globally readable (`USING (true)`) and platform-write-only: it is deployment
  config, and the property worth protecting is that an organization cannot _toggle_ a flag. Only
  `platform_audit_events` is platform-only in both directions. `app.platform` is set in exactly one
  place, never inside a request transaction, and an architecture test scanning `src/`/`infra/` fails
  the build on a second call site. Do not "correct" any of these to a plain org policy or to no RLS.
- Background jobs must establish organization context explicitly and throw when it is missing.
- Any read of `entitlements`, `capability_cohorts`, or `platform_audit_events` that needs rows beyond
  the ambient org must assert platform scope and throw when it is missing. RLS filters rather than
  raises, so a scope-less read returns zero rows with no error — and "no row" is a meaningful value
  here (use the default / not entitled / not in the cohort), so the empty read renders as a coherent
  wrong answer rather than a failure. The same applies to writes on all four tables: an
  `UPDATE`/`DELETE` off the tracked-entity path must assert an affected-row count, because RLS filters
  those to zero rows silently too.

### UX Contract

These flows are instrumented in telemetry; regressions fail the release checklist.

- Record a tenant payment in no more than 3 interactions.
- Owner ending balances are visible at 0 clicks.
- Uncleared trust items are visible in no more than 1 click.
- Start reconciliation in no more than 2 clicks, completed in place.
- Issue a refund check in no more than 4 interactions, including printing it.
- UI must use design tokens and primitives ported from the prototype.
- Money renders with tabular numerals.
- Status is never conveyed by color alone.

## Working Conventions

- **Work is tracked in GitHub Issues, not in Markdown checkboxes.** The LeaseBook project board
  (`gh project view 3 --owner jwh3times`) is the single view across both trackers; its `Track` and
  `Gate` fields carry the sequencing that `private/roadmap.md` §2 used to describe in prose. Close
  issues as work completes. Scope changes are new or edited issues, not side conversations. An issue
  labelled with a gate blocks the work that depends on it until it is closed.
- The Definition of Done in `CONTRIBUTING.md` applies to every task: tests at the right altitude,
  audit/telemetry events on
  money-touching paths, empty/loading/error states, keyboard path, and demoability on the seed org.
- Scope discipline: `docs/product-scope.md` is the public product boundary. Additions outside its
  explicit non-goals require a formal scope change before implementation; unpublished commercial or
  customer consequences are recorded privately.
- ADRs live in `docs/adr/`. Every deviation from a `docs/blueprint.md` default gets a short
  ADR.
- Seed data is sacred. The demo dataset, ported from the prototype's `data.jsx`, doubles as the
  golden-file test fixture. Its figures reconcile to the cent and validate the accounting engine.
  Do not casually edit seed numbers. If they change, golden tests change deliberately in the same
  work.
- Accounting-adjacent changes must run the invariant, property-based, and golden-file suites.
- If a budgeted UX flow changes, add or update the corresponding Playwright e2e coverage.
- Before work in a domain, read its guidance file from the table under Domain Guidance Files; for
  pre-merge review, lead with findings.
- When documentation accuracy is affected or in doubt, run the `docs-updater` skill
  (`.agents/skills/docs-updater/SKILL.md`) and update only the docs that actually drifted.

## Agent Operating Notes

- Use `rg` or `rg --files` for searches whenever available.
- Do not overwrite user changes. Check `git status --short` before editing and review existing
  diffs in files you need to touch.
- Add tests proportional to risk. Money, organization isolation, RLS, posting, and migration changes require higher
  confidence than presentational changes.
- Do not put confidential `private/` details into public commits, PR descriptions, public docs, or
  generated output.
