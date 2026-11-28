# AGENTS.md

The canonical cross-agent engineering contract. Tool-specific instruction files may add integration
details but defer to this file for rules, commands, invariants and documentation ownership.

## Repository State

- **Work is tracked in GitHub Issues, not Markdown checkboxes; the project board (project 3) is
  the live source of truth for open work.** Do not trust summaries, this one included. Close issues
  as work completes; scope changes are new or edited issues. A gate-labelled issue blocks the work
  depending on it until it closes.
- Operator-gated work (Azure OIDC, ACR, the deploy workflows, live Key Vault and managed identity,
  the first PITR drill, deployment telemetry, ADR-041 first-apply) is deferred, not ordinary
  engineering work.
- Payments run in development fixtures only, with no live provider: read
  `docs/runbooks/payment-simulation.md` before running or resetting one.
- `private/` is a gitignored confidential checkout whose locator stays out of this repository; if it
  is absent and the task needs it, ask the user for the PRD or design context.
- Confidential material (pricing, strategy, customer identity, internal analysis) belongs in
  `private/`, never in public commits, PR text, docs or generated output. Commit and push the two
  repositories independently.

## Ground Rules

Verify, do not assume.

- Do not build on unverified assumptions. When a task depends on a fact that cannot be confirmed
  from code, docs, or a quick local check, stop and verify before designing against it.
- This is especially important for trust-accounting and domain facts: NCREC trust-money rules
  (21 NCAC 58A .0116-.0118; record retention is .0108), basis behavior, posting-template lines,
  prototype golden figures, demo-seed numbers, EF/RLS/posting runtime behavior, and anything that
  can affect fiduciary correctness.
- Ground truth is usually local: scope in `private/LeaseBook_PRD_v1.0.md` and
  `private/claude_design_files/`; built or decided work in closed issues and
  `private/planning/*_retro.md` (the record of how completed work went); real figures from
  `check-invariants`, the golden/invariant/property suites, the seeded demo org, or the migration,
  RLS-helper and posting-template code.
- A structure built to discover an unknown at runtime is still an assumption: verify it against real
  data such as journal replay or the golden dataset.
- A low-stakes default is fine: state it and proceed. If being wrong would force rework, move a
  golden figure, or ship fiduciary incorrectness, verify or ask.

## Commands

Before a command not listed here, read its owning doc: `docs/runbooks/local-dev.md` (single tests,
the API, `scripts/dev.ps1`, e2e, seeds, the container), `docs/testing/` (coverage, mutation,
scenarios), `docs/perf.md`, or `docs/runbooks/payment-simulation.md`; traps are in
`docs/agents/local-runs.md`. The `capabilities` CLI is the only capability write surface (ADR-028).
When a port changes, update the `README.md` Port map and the configs it cites together.

- Build: `dotnet build LeaseBook.slnx -c Debug`
- All tests: `dotnet test --solution LeaseBook.slnx`
- Format check: `dotnet format --verify-no-changes --exclude src/LeaseBook.Web/Migrations`
- Web, from `web/`: `npm ci`, then the `web/package.json` scripts; docs gate `npm run docs:check`

### Database (run from repo root)

Run `dotnet tool restore` once for `dotnet-ef`. Migrations run as the migrator role (in Docker, a
one-shot `migrator` image), never at app startup. Seed and check with
`$env:ASPNETCORE_ENVIRONMENT='Development'` set.

- Apply migrations as migrator role: `dotnet ef database update --project src/LeaseBook.Web --context AppDbContext`
- Seed demo org: `dotnet run --project src/LeaseBook.Web -- seed --org demo`
- Check invariants: `dotnet run --project src/LeaseBook.Web -- check-invariants --org demo` (every org: `--all`)

## Domain Guidance Files

Before editing a domain, read its guidance file in `.claude/agents/` and apply its rules, whichever
harness you are in; where one conflicts with an invariant here, the invariant wins.

- `dotnet-api.md`: .NET features, endpoints, commands/queries, integration tests
- `react-frontend.md`: React components, hooks, design tokens, frontend tests
- `postgres-specialist.md`: migrations, RLS policies, DB schema design, Postgres queries
- `trust-accounting.md`: accounting posting logic, journal entries, trust equation changes
- `code-reviewer.md`: reviewing a diff for correctness bugs before merging
- `azure-infrastructure.md`: Azure infra, Bicep, deploy workflows, Key Vault, managed identity, PITR

**Edit `.claude/agents/` and `.agents/skills/` only**, never the generated `.codex/agents/` and
`.claude/skills/`, which the next sync overwrites. Then run `npm run docs:format` (from `web/`)
before `node scripts/sync-agents.mjs`, since formatting after the sync re-drifts the mirror, and
commit the result. Keep `.claude/skills/` plain copied files, never reintroducing a symlink there
(`docs/agents/local-runs.md`, "The docs gate").

When a change affects docs, ports, ADR-worthy decisions, user workflows, commands, or business
events, update the relevant docs in the same change. When accuracy is affected or in doubt, run the
`docs-updater` skill and update only the docs that actually drifted. It and `/open-pr` are
session-scoped: no harness is guaranteed to run them, and CI does not.

## Agent skills

### Development flow

<!-- shared:development-flow -->

The flow skills are this repository's binding process for changing code. Read
`.agents/skills/open-pr/settings/dev-flow.md` (the routing, exemptions and issue linking) and
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
- **The flow ends at `/open-pr`.** `/implement` stops at a commit; `/open-pr` refreshes the docs,
  writes the changelog and the PR body, and opens the PR (Claude Code's built-in `/ship` is a
  different command, outside this flow). When the user says to merge it, `/land` merges it
  and confirms its release. `/dependabot` reviews the open Dependabot PRs and hands the ones the
  user names to `/land`; nothing merges a Dependabot PR on its own. `/ask-matt` also routes to
  `/pr`, `/retro` and `/implement-spec`, which are not installed here.
- **Tests are proven, reds are read.** Before `/implement` commits a change that adds or changes
  tests, and in `/code-review`, run `/test-prover`: in a scratch worktree it reverts the fix and
  breaks its guards, and every new test must go red. `/open-pr` runs it too. `/land` and
  `/dependabot` read a red check with `/ci-triage`, which never re-runs anything.
  `/records-audit` checks the board, the trackers, the follow-up wiki and the changelog against
  each other; run it weekly, and `/what-next` says when it is due.
- **`/handoff` is the shared skill.** It parks the session for the next one, on either machine or
  harness, closes it out with `/end-session`, and `/lets-go` resumes it. It does not write the
  portable file `/ask-matt` describes, so a `/prototype` detour stays in the session or uses a
  branch.
- **A wizard is not the record.** A step only a human can take follows the `## Procedure` in
  `.agents/skills/open-pr/settings/human-follow-up.md`, whose instructions stand without the script.
- **Research goes where it is safe to keep.** `/research` findings that would expose private material
  go to the private companion, where there is one, never to an ignored folder with no repository
  behind it. Details of an unpublished vulnerability are a draft advisory, never a research file.

<!-- /shared:development-flow -->

### Shared skills

<!-- shared:shared-skills -->

`end-session`, `handoff`, `lets-go`, `open-pr`, `land`, `dependabot`, `docs-updater`, `what-next`,
`test-prover`, `ci-triage`, `records-audit` and `tldr` install from `jwh3times/skills`, and are
never edited here: reinstalling overwrites a local edit.
A fix goes upstream, and what they need to know about this repository goes in the settings files
under `docs/agents/`, which each skill proposes on first run.

The other shared skills come from one `mattpocock/skills` snapshot (v1.3.2, pinned by commit), the
same in every repository, and are never edited here either: what they need to know goes in this
file or `docs/agents/`. Moving to a newer snapshot is a change in `jwh3times/skills`. Keep
`.agents/skills/` out of every formatter: `scripts/check-skills.mjs` fails when an installed copy
differs from `skills-lock.json`, or the lock from these commands.

Re-running the install is the update; never run `npx skills update`, which leaves `.claude/skills/`
linked to its source instead of copied:

```bash
npx skills add jwh3times/skills -s end-session -s handoff -s lets-go -s open-pr -s land -s dependabot -s docs-updater -s what-next -s test-prover -s ci-triage -s records-audit -s tldr -a codex --copy -y
npx skills add mattpocock/skills#d1478fcb140db0b93c0c8df17bcfd311844749d0 \
  -s ask-matt -s code-review -s codebase-design -s diagnosing-bugs -s domain-modeling -s grill-me \
  -s grill-with-docs -s grilling -s implement -s improve-codebase-architecture -s prototype \
  -s research -s setup-matt-pocock-skills -s tdd -s teach -s to-questionnaire -s to-spec \
  -s to-tickets -s triage -s wait-what -s wayfinder -s wizard -s writing-for-agents \
  -a codex --copy -y
node scripts/sync-agents.mjs
```

`scripts/sync-agents.mjs`, `lint-agent-neutrality.mjs`, `check-docs.mjs`, `check-skills.mjs`,
`guard.mjs`, and `sync-main.mjs`, `next-version.mjs`, `check-changelog.mjs` and
`stamp-changelog.mjs` where this repository carries them, are canonical in `jwh3times/skills` with
their tests: fix them there, then copy them here.
`sync-agents.mjs` also writes the marked blocks in this file: change them in `jwh3times/skills`, not
here. It registers `guard.mjs` in `.claude/settings.json`, which stops the commands and edits these
rules forbid; this repository's own additions go in `docs/agents/guard.md`.

<!-- /shared:shared-skills -->

### Issue tracker

Route work by **whether the text is safe in public git history**: public by default, the private
tracker (locator kept out of this tree) for confidential work and every human follow-up, a draft
advisory for an unpatched weakness ([`docs/agents/issue-tracker.md`](docs/agents/issue-tracker.md)).

- Never reference a private issue from a public PR, commit, or issue: the reference leaks it.
- An issue states its own question, never another issue's status; relate them with task lists and
  `Part of #N`.
- Before reporting agent work complete, execute the human-action handoff
  ([`docs/agents/human-follow-up.md`](docs/agents/human-follow-up.md)): private follow-up issues on
  project 3, fully labelled and fielded, with published step-by-step wiki procedures indexed by
  `human-todo`.
  Verify and report both links, or report the handoff incomplete with its blocker.
- Deployment follow-ups all go into the single private public-distribution issue and its wiki page,
  never a separate item or page.

### Triage labels

Default label vocabulary: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`,
`wontfix`. See [`docs/agents/triage-labels.md`](docs/agents/triage-labels.md).

### Domain docs

Single-context layout: `CONTEXT.md` and `docs/adr/` at the repo root. See
[`docs/agents/domain.md`](docs/agents/domain.md).

### MCP servers

Query `microsoft-learn` (.NET, Azure) or `context7` (other libraries) before designing against an
API from memory, with no `private/` or confidential detail in the query: both send it outside.
GitHub work goes through `gh`. Edit `.mcp.json` only, then run `node scripts/sync-mcp.mjs`. Before
using `playwright` or changing a server, read [`docs/agents/mcp.md`](docs/agents/mcp.md).

## Architecture

`docs/architecture.md` is the canonical public map of the implemented system. `docs/blueprint.md` is
the historical pre-M0 baseline; accepted ADRs and implemented behavior supersede it where the system
evolved. These test-enforced patterns bind future modules:

- Modular monolith: an ASP.NET Core host plus one project per module (`Accounting`, `Directory`,
  `Banking`, `Reporting`, `Operations`, `Capabilities`, `Payments`, `SharedKernel`, `Migrator`).
  Modules reference `SharedKernel` only (`ModuleBoundaryTests`), which stays pure cross-cutting
  primitives; module contracts do not live there.
- Cross-module calls go through `Contracts` ports so modules stay extractable. A feature module
  must never read another module's tables or data directly: no cross-module SQL, no cross-module
  LINQ, and no referencing another module's entity types. Cross-module reads use a port declared in
  the consuming module's `Contracts`, implemented by a host adapter delegating via `ISender`. The
  reporting/read layer is the sole intentional exception, under its own ADR.
- Ports expose batch reads that return maps, never per-id reads, except a posting's read of the one
  tenant or property it names (ADR-014 addendum of 2026-11-14).
  Adapters run on the ambient RLS transaction and
  must not open a new connection.
- CQRS vertical slices: one file per feature with its record, `AbstractValidator` and handler.
  Minimal APIs only (`IEndpointModule`, `TypedResults`), a hand-rolled `ISender`; no MediatR or
  AutoMapper.
- Within a module, `db.Database.SqlQuery<T>` is acceptable for that module's own analytical reads.
  Prefer EF LINQ for simple reads; reserve raw SQL for window functions, `FILTER`, trigram queries,
  or other queries LINQ cannot express.
- Accounting gets the highest test rigor. Its double-entry journal is written only through posting
  templates keyed to business events, lines tagged cash/accrual/both so each basis is a query.
  Ledgers, registers and statements are journal projections, never independently maintained state.
- The stack is PRD-locked: .NET current LTS, React and TypeScript with Vite, PostgreSQL, Azure
  Container Apps in East US 2, Key Vault and managed identity. Changing one, or deviating from any
  other default tabled in `docs/blueprint.md`, requires a short ADR.

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
- Capabilities gate **reachability only** (ADR-028): whether a posting path runs, never its lines or
  amounts. No capability-derived value may become an argument to an Accounting command, business
  event, or posting-template input; money-affecting parameters belong in `OrgSettings`. A
  multi-step run
  resolves the capability set once and passes it explicitly, never re-asking mid-run.

### Organization Isolation

- Postgres RLS is the security boundary. EF global query filters are ergonomics only.
- Organization context is set with `SET LOCAL app.org_id` inside the transaction. Never use
  session-level `SET`: pooled connections would leak context.
- `OrgScopedExecutor` is the only production setter of the org context (ADR-048): one
  transaction-local `set_config` sets `app.org_id`, `app.persona`
  (`system`/`staff`/`tenant`/`owner`/`none`) and `app.user_id`. `PersonaResolver` maps roles, a
  mixed or unrecognised set to `none`; `OrgContextCallSiteTests` fails a second setter.
- Missing org context fails closed, and so does a missing persona: every session reading org data,
  an operator's `psql` or pgAdmin included, must set `app.persona` (e.g. `staff`) as well as
  `app.org_id`, or it sees zero rows.
- Three DB roles: `leasebook_migrator` (owns `public`, migrations only), `leasebook_app` (runtime,
  under `FORCE ROW LEVEL SECURITY`), `leasebook_ops` (read-only).
- `leasebook_app` deliberately owns the `hangfire` schema (ADR-001), its only `CREATE` privilege.
  Do not "correct" it to migrator ownership: that breaks the first Hangfire upgrade. Nothing
  org-scoped lives there.
- Every new org-scoped table goes through the migrations RLS helper: column, `USING`/`WITH CHECK`
  policy, `FORCE ROW LEVEL SECURITY` and the deny-by-default persona gate (ADR-048) in one call.
  The gate admits `staff` and `system` org-wide, and tenants and owners only through a migration's
  explicit `Rls.ApplyPersonaGate` grant, derived from their active `owner_access`/`resident_access`
  link, never from an id the application sets. The ADR-003 application-layer checks remain the
  first layer.
- A schema guard test fails CI if any `org_id` table lacks its policy or persona gate, and it pins
  every persona-gate predicate exactly.
- `FORCE ROW LEVEL SECURITY` binds the migrator too, so a migration that rewrites data must bracket
  its DML with `ALTER TABLE <t> NO FORCE ROW LEVEL SECURITY` / `... FORCE ROW LEVEL SECURITY` in
  the same `Sql` block, for every forced table the statement names, not just the target. EF's
  `InsertData`/`UpdateData`/`DeleteData` builders are not usable for this, since no block can
  bracket them. An architecture test fails CI on a missing lift, a missing restore, and those
  builders.
- The four platform capability tables are gated on `SET LOCAL app.platform` (ADR-028) in three
  deliberate shapes: `entitlements` and `capability_cohorts` keep `org_id` and an org read policy
  plus a platform escape; `feature_flags` is globally readable and platform-write-only; only
  `platform_audit_events` is platform-only in both directions. `app.platform` is set in exactly one
  place, never inside a request transaction, and an architecture test scanning `src/`/`infra/`
  fails the build on a second call site. Do not "correct" any of these to a plain org policy or to
  no RLS ([why](docs/architecture.md#the-capability-seam)).
- Background jobs must establish organization context explicitly and throw when it is missing.
- Any read of `entitlements`, `capability_cohorts` or `platform_audit_events` that needs rows beyond
  the ambient org must assert platform scope and throw when it is missing, and an `UPDATE`/`DELETE`
  on any of the four tables off the tracked-entity path must assert an affected-row count: RLS
  filters both to zero rows silently. The reasons for these rules are in
  [`docs/architecture.md`](docs/architecture.md#organization-isolation-and-security).

### UX Contract

These flows are instrumented in telemetry; regressions fail the release checklist.

- Record a tenant payment in at most 3 interactions; owner ending balances visible at 0 clicks;
  uncleared trust items within 1 click; start reconciliation within 2 clicks, completed in place;
  issue a refund check, printing included, within 4 interactions.
- UI must use design tokens and primitives ported from the prototype.
- Money renders with tabular numerals.
- Status is never conveyed by color alone.

## Working Conventions

- The Definition of Done in `CONTRIBUTING.md` applies to every task: tests at the right altitude,
  audit/telemetry events on money-touching paths, empty/loading/error states, keyboard path, and
  demoability on the seed org.
- `docs/product-scope.md` is the product boundary: additions outside its non-goals need a formal
  scope change first, with commercial or customer consequences recorded privately.
- Seed data is sacred: the demo dataset is the golden fixture, reconciled to the cent. Never casually
  edit seed numbers; if they change, golden tests change deliberately in the same work.
- Accounting-adjacent changes must run the invariant, property-based, and golden-file suites.
- If a budgeted UX flow changes, add or update the corresponding Playwright e2e coverage.

## Agent Operating Notes

- Do not overwrite user changes. Check `git status --short` before editing and review existing
  diffs in files you need to touch.
- Add tests proportional to risk: money, organization isolation, RLS, posting, and migration changes
  require higher confidence than presentational ones.
- Before trusting a new test, guard or claim, read `docs/agents/verification.md`; before running
  tests, e2e or the docs gate locally, `docs/agents/local-runs.md`; once a PR is open,
  `docs/agents/merging.md`.
