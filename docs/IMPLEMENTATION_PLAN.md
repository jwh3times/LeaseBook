# Roadmap Implementation Plan — remaining work as of 2026-07-03

- **Date:** 2026-07-03
- **Branch:** `claude/roadmap-implementation-plans-o9740c`
- **Sources:** `git log` on `main` (PRs #26–#65), `docs/adr/ADR-000…023`, `docs/superpowers/specs/`
  and `docs/superpowers/plans/` (M5–M8), `docs/runbooks/`, `docs/migration/`, `infra/`,
  `.github/workflows/`, and the actual module/test/e2e source trees.
- **Authority note:** `private/TODO.md` is the **canonical** build plan and is gitignored — it is
  **absent from this clone**. This document is a committed, engineering-only snapshot derived from
  committed evidence; where scope lives only in `private/` (Payments, any unlisted M8 items) that is
  flagged explicitly rather than reconstructed. When this plan and `private/TODO.md` disagree, the
  private TODO wins; the maintainer should reconcile both. Per the `private/` rule, this file carries
  no pricing, strategy, or customer detail.

---

## 1. Ground-truth status

CLAUDE.md's "Repository state" section still says *"M5 (Owner Statements & Reporting) is the current
frontier"* and that Reporting/Operations/Payments are scaffolded shells and Migrator a placeholder.
That summary **lags reality by three milestones** (CLAUDE.md itself warns it lags). Committed
evidence:

| Milestone / feature | Status | Evidence (commits · files) |
| --- | --- | --- |
| M0–M4 (foundations, trust engine, Directory, ledger hub, Banking & Reconciliation) | **Built** | PR #26 merge `dde81ca`; M5-prep close-out PR #30 `8a9d3e7`; CHANGELOG `[Unreleased]` covers exactly this range |
| M5 — Owner Statements & Reporting | **Built** | PR #31 `e2c7778` (`ad67ef7`…`96156d6`); ADR-016; `src/LeaseBook.Modules.Reporting/` (catalog, statement engine + tie-out gate, QuestPDF PDF + CSV rendering, delivery seam + immutable artifact store, RLS migration); `web/e2e/m5-reports.spec.ts` |
| M6 — Bulk Operations (rent/late-fee/disbursement runs) | **Built** | PR #34 `19878a4` (`4999d26`…`0a1eccf`); ADR-017/018/019; `src/LeaseBook.Modules.Operations/` (`Runs/RunEngine.cs`, `RentRunStrategy`, `LateFeeRunStrategy` w/ NC §42-46 clamp, `DisbursementRunStrategy` + `MgmtFee`, `Proration`, `bulk_runs` tables, `IBatchPosting` port); `tests/LeaseBook.Tests.Operations/`; `web/e2e/m6-*.spec.ts` |
| M7 — Migration toolkit + import-first onboarding | **Built** | PR #36 `7a1ead0` (`d4eea6b`…`9127af0`); ADR-020/021; `src/LeaseBook.Migrator/` (CsvImporter, AppFolio profiles, EntityImporter), staging tables + clearing CHECK migration, balance import via clearing, verification report + hard sign-off gate, onboarding wizard; `tests/LeaseBook.Tests.Migrator/`; `docs/migration/{appfolio,parallel-run}.md`; `web/e2e/m7-onboarding.spec.ts` |
| Security hardening (CSV formula-injection, seeder env guard) | **Built** | PR #37 `1a83bd2` (`7ad27f7`, `ab75aa3`) |
| M8.0 — `azure-infrastructure` specialist agent | **Built** | PR #48 `3d8c92e` (`cdf7eb6`…`b1326f0`); `.claude/agents/`; registered in CLAUDE.md |
| M8 — Bicep infra + deploy workflows (**authored**, not live) | **Built (authoring)** | `infra/main.bicep` + `infra/modules/{monitoring,registry,database,vault,storage,containerapp}.bicep` + `infra/env/{dev,prod}.bicepparam` + `infra/db/bootstrap.sql` + `infra/db/azure-bootstrap.md`; `.github/workflows/deploy-dev.yml`, `deploy-prod.yml` (both self-describe enablement as operator-deferred) |
| M8.2 spec #1 — e2e in CI + a11y gate (plan `2026-06-30-m8-ci-e2e-a11y-gate.md`) | **Built** | PR #54 `c95a557` (`9874077`…`4d19637`); ADR-022; `ci.yml` `e2e` job (seeds demo + cutover orgs, runs full Playwright suite); `web/e2e/a11y.spec.ts` (WCAG 2 AA gate across all routed pages, light theme) |
| M8.2 spec #2 — visual regression (plan `2026-06-30-m8-visual-regression.md`) | **Built** | PR #59 `83cf92c` (`a4b7d37`…`562a1c3`); ADR-023; `visualSnapshot` helper in `web/e2e/helpers.ts`; 7 gated states inline in the milestone specs; committed Linux baselines in `web/e2e-snapshots/`; `.github/workflows/update-visual-baselines.yml` re-baseline dispatch |
| Payments module | **Pending — shell** | `src/LeaseBook.Modules.Payments/` contains only `ModuleMarker.cs` + empty `Contracts/`; Phase 2 per CLAUDE.md; no committed scope docs |
| Live Azure environments (OIDC, ACR, Container App, Key Vault, role bootstrap) | **Pending — operator-gated** | deploy workflows' header comments; `infra/README.md` ("Deployment is gated on operator Azure access") |
| First PITR restore drill | **Pending — operator-gated** | `docs/runbooks/restore.md` is a skeleton with an explicit `TODO (first drill)` |
| Prod DB private networking (VNet delegation + private DNS) | **Pending — engineering-authorable gap** | `infra/README.md` §Production networking and `infra/modules/database.bicep` note prod must wire `delegatedSubnetResourceId` + a private DNS zone; no `network` module exists in `infra/modules/` |
| Dark-theme a11y coverage | **Pending — deferred follow-on** | ADR-022 Consequences: "axe scan covers the default (light) theme only; dark-theme accessibility is a tracked follow-on" |
| Dark-theme visual coverage | **Pending — deferred follow-on** | ADR-023 Consequences: "Dark-theme visual coverage is deferred (tracked with the dark-theme a11y follow-up)" |
| Extended e2e coverage (Directory nav, error states, keyboard-only sequences) | **Pending — deferred follow-on** | ADR-022 Consequences: "Deferred (follow-on specs): … extended e2e coverage" |
| CHANGELOG `[Unreleased]` | **Drifted** | `CHANGELOG.md` records only through M4 (Banking); no M5/M6/M7/M8 entries |
| CLAUDE.md "Repository state" | **Drifted** | Still claims M5 is the frontier and Reporting/Operations shells; last substantive refresh `2cd69dd` (M5-prep) |
| TS6 upgrade unblock | **Automated — no action** | `.github/workflows/ts6-unblock-watch.yml` files a tracking issue when `openapi-typescript` admits TypeScript 6 |
| Source TODO comments | **Near-clean** | Only two hits: `src/LeaseBook.Web/Dashboard/DashboardService.cs:12` (stale doc-comment: "(TODO M2.4)" label + "Reporting stays dormant" claim) and `src/LeaseBook.Modules.Accounting/Persistence/JournalLineConfiguration.cs:58` (a reference to `private/TODO.md` §1, not an open task) |

**Conclusion:** M0–M7 are complete and merged. M8 is substantially complete on the
engineering-authorable side (agent, Bicep authoring, deploy workflows, CI e2e + a11y gate, visual
regression). What remains: the operator-gated go-live track, four engineering-ready follow-ons
(docs catch-up, dark-theme a11y + visual, extended e2e, prod-networking Bicep), and the Phase-2
Payments module whose scope lives only in the private PRD.

---

## 2. Sequencing & dependencies

### Engineering-ready (no operator/Azure access required)

1. **Item A — Documentation drift catch-up** (S). Do first: every later PR's docs-updater Stop hook
   otherwise fights the same stale baseline.
2. **Item B — Dark-theme a11y gate** (M). Independent of A.
3. **Item C — Dark-theme visual regression** (S). **Depends on B** — baseline only states already
   passing the dark-theme axe gate, per ADR-023's coupling note.
4. **Item D — Extended e2e coverage** (M). Independent; can run in parallel with B/C.
5. **Item E — Prod DB private networking Bicep** (M). Authoring is engineering-ready; *validation
   beyond `az bicep build` / `what-if`* needs operator subscription access. Do before the prod leg of
   Item F.

### Operator-gated (needs Azure/GitHub-org access; engineering supports)

1. **Item F — Live environment enablement + first deploys** (dev, then prod). Consumes the authored
   Bicep + workflows; prod leg wants Item E first.
2. **Item G — First PITR restore drill**. Depends on a live dev environment (Item F dev leg).

### Blocked on private scope

1. **Item H — Payments module** (L). Blocked on the maintainer supplying the Phase-2 scope from
   `private/LeaseBook_PRD_v1.0.md` / `private/TODO.md`. Outline-level plan only, below.

---

## 3. Item A — Documentation drift catch-up

**Objective.** Bring the committed docs back in sync with what shipped (M5–M8), so CLAUDE.md's
"Repository state", the CHANGELOG, and stale in-code doc comments stop misleading sessions and
readers.

**Current state.**

- `CHANGELOG.md` `[Unreleased]` → `### Added` ends at Banking & reconciliation (M4). Nothing for M5
  (owner statements/reporting), M6 (bulk operations), M7 (migration toolkit/onboarding), M8 (CI e2e +
  a11y gate, visual regression), or the PR #37 security hardening (`### Security` is absent).
- `CLAUDE.md` "Repository state": claims M5 is the frontier; claims `Reporting`, `Operations`,
  `Payments` are scaffolded shells and `Migrator` a placeholder — three of those four are now built.
- `src/LeaseBook.Web/Dashboard/DashboardService.cs:12` doc comment says "(Reporting stays dormant)"
  and cites "(TODO M2.4)" — both stale.
- `docs/adr/README.md` index is current through ADR-023 (self-healed) — no action.

**Design / approach.** Pure docs pass — invoke the `docs-updater` subagent (its charter). No ADR.
Keep CHANGELOG entries in Keep-a-Changelog vocabulary, one bullet block per milestone mirroring the
existing M0–M4 style; add a `### Security` section for the CSV-injection/seeder-guard fixes. Rewrite
CLAUDE.md "Repository state" to: M0–M7 complete, M8 engineering side complete
(agent/Bicep/workflows/CI gates), operator-gated remainder outstanding, Payments the only remaining
shell. Do **not** touch `private/` (absent here anyway).

**Tasks.**

1. `CHANGELOG.md` — add `### Added` bullets for M5, M6, M7, M8 (CI e2e + a11y gate; visual
   regression; authored Azure infra + deploy workflows), and a `### Security` section (PR #37).
2. `CLAUDE.md` — rewrite the "Repository state" bullets (frontier → operator-gated M8 remainder +
   Payments/Phase 2); fix the "scaffolded shells" sentence; keep the "consult private/TODO.md, not
   this summary" caveat.
3. `src/LeaseBook.Web/Dashboard/DashboardService.cs` — fix the stale doc comment (drop "Reporting
   stays dormant"; replace "(TODO M2.4)" with plain prose). Comment-only change; no behavior.
4. Sweep `README.md` for the same staleness (its e2e/CI sections were updated in PR #54/#59; verify
   the module/status prose and the Port map still hold — no port changed, so likely no edit).

**Testing.** `dotnet format --verify-no-changes --exclude src/LeaseBook.Web/Migrations` (comment edit
is in C#); `npx prettier --check` via the repo's format gate for the `.md` files; full CI as usual.
No behavioral tests needed.

**Docs updates.** This item *is* the docs update. Note for the maintainer: tick the corresponding
`private/TODO.md` M5–M8 checkboxes and prepend `private/planning/` retros locally — cannot be done
from this clone. Open question for the maintainer, recorded not decided: whether to cut a first
tagged release (roll `[Unreleased]` into `0.1.0`) now that M0–M7 shipped — a deliberate separate
step per CLAUDE.md.

**Risks & open questions.** Only risk is describing M5–M8 scope inaccurately — source each bullet
from the merged PRs and ADRs listed in §1, not memory. **Size: S.**

---

## 4. Item B — Dark-theme accessibility gate (ADR-022 follow-on)

**Objective.** Extend the CI-gated axe scan (WCAG 2 AA) to the dark theme, closing the explicitly
tracked gap in ADR-022 ("axe scan covers the default (light) theme only").

**Current state.**

- `web/e2e/a11y.spec.ts` runs `@axe-core/playwright` across all routed pages in the default (light)
  theme; it is a merge gate via the `e2e` job in `.github/workflows/ci.yml`.
- The theme system: `web/src/design/ThemeProvider.tsx` reads `localStorage['leasebook.theme']`
  (`{ theme: 'light' | 'dark', accent, density }`, falling back to `prefers-color-scheme`) and stamps
  `document.documentElement.dataset.theme`. Tokens live in `web/src/design/tokens.css`.
- ADR-022's triage rule (D4): fix every reasonable violation; a genuinely out-of-scope cluster
  becomes a documented `exclude` selector with an inline reason + follow-up — never a silent skip.

**Design / approach.**

- Force dark mode deterministically in Playwright via
  `page.addInitScript(() => localStorage.setItem('leasebook.theme', JSON.stringify({ theme: 'dark' })))`
  (storage-driven, so it wins over `prefers-color-scheme`); optionally also set
  `colorScheme: 'dark'` in the context for native widgets.
- Reuse the existing route list and `runA11y` harness — parametrize the existing spec by theme
  rather than duplicating the file, so the route inventory can't drift between themes. Expect the
  dominant failure class to be color-contrast in `tokens.css` dark values; fixes are **token edits
  only** (the design-token invariant: no per-component color overrides, status never by color alone).
- CI cost roughly doubles the a11y portion of the `e2e` job — acceptable (ADR-022 already accepts
  e2e as the slowest job); if runtime becomes a problem, scan dark theme on the same page visit
  (toggle theme in-page, re-run axe) instead of re-navigating.
- **ADR:** no new ADR — this executes ADR-022's documented follow-on. Update ADR-022's Consequences
  line ("not yet guarded") when it lands.

**Tasks.**

1. `web/e2e/helpers.ts` — add a `withTheme(page, 'dark')` (or `gotoWithTheme`) helper that seeds
   `localStorage['leasebook.theme']` via `addInitScript` before navigation.
2. `web/e2e/a11y.spec.ts` — wrap the existing per-route scan in a `for (const theme of ['light', 'dark'])`
   describe block (light branch identical to today's behavior).
3. Run locally (`npm run e2e -- a11y.spec.ts`) against the seeded host; fix violations:
   - contrast fixes in `web/src/design/tokens.css` dark-theme custom properties (mirror how
     `8f2a270` fixed the light `--text-2` muted-text hierarchy);
   - any non-contrast violations in the affected components under `web/src/components/` /
     `web/src/features/` (consult the `react-frontend` agent's rules first).
   - Out-of-scope clusters → documented `exclude` with reason + follow-up (rule D4).
4. `docs/adr/ADR-022-e2e-in-ci-and-a11y-gate.md` — amend the Consequences bullet to state dark theme
   is now guarded.

**Testing.** `npm run e2e -- a11y.spec.ts` green in both themes locally and in the CI `e2e` job;
`npm run lint`, `npm run typecheck`, `npm run test` (jsdom unit tests must not regress from token
changes); visual baselines (`web/e2e-snapshots/`) must **not** change — the gated states are
light-theme; if a token edit accidentally moves a light-theme pixel, that's a regression to fix, not
re-baseline.

**Docs updates.** CHANGELOG `[Unreleased]` `### Added` (dark-theme a11y gate); ADR-022 amendment;
CLAUDE.md only if the testing section's wording needs it; maintainer ticks `private/TODO.md`.

**Risks & open questions.** Dark-token contrast fixes can shift the visual identity — coordinate
with the design prototype (`private/claude_design_files/`, maintainer-verified) before large token
changes. Accent variants (`teal|violet|green|navy`) × density are **not** in scope — gate the
default accent only, note the matrix as a possible future exclusion-tested follow-up. **Size: M.**

---

## 5. Item C — Dark-theme visual regression (ADR-023 follow-on)

**Objective.** Extend the CI visual gate to dark-theme renders of (a subset of) the 7 gated states,
closing ADR-023's deferred item.

**Current state.** `visualSnapshot` helper in `web/e2e/helpers.ts`; 7 light-theme snapshot calls
inline in `web/e2e/{budgeted-flows,m3-ledger,m4-banking,m5-reports,m7-onboarding}.spec.ts`; Linux
baselines committed under `web/e2e-snapshots/`; re-baseline via
`.github/workflows/update-visual-baselines.yml` (workflow_dispatch on `main`); 2% tolerance +
masking (dashboard "Collected this month" card, ledger composer Date field) per ADR-023.

**Design / approach.** Depends on Item B (only snapshot states that pass the dark axe gate, and
reuse its `withTheme` helper). Don't double all 7 states blindly — pick the theme-sensitive ones
(dashboard full, ledger composer, owner-statement full — the states where token regressions would
show) and add `-dark` variants via a `theme` option on `visualSnapshot` that suffixes the snapshot
name. Baselines are CI-rendered Linux only (ADR-023); bootstrap the new dark baselines the same way
as `562a1c3` (from the CI run's actuals) or via the dispatch workflow, which needs no change (it
regenerates whatever `toHaveScreenshot` calls exist). **ADR:** no new ADR; amend ADR-023's
Consequences when done.

**Tasks.**

1. `web/e2e/helpers.ts` — `visualSnapshot(page, name, { theme })` name-suffixing (or accept the
   pre-seeded theme from Item B's helper and pass explicit `-dark` names).
2. Add dark `visualSnapshot` calls to the chosen specs (start with
   `budgeted-flows.spec.ts` dashboard-full and `m5-reports.spec.ts` owner-statement-full).
3. Generate + commit baselines through the `update-visual-baselines.yml` dispatch (post-merge of the
   spec change, per the workflow-on-main constraint noted in `fd86450`).
4. `docs/adr/ADR-023-visual-regression.md` — amend Consequences.

**Testing.** CI `e2e` job green with the new baselines; masking rules verified on the dark renders
(dynamic figures still masked); confirm re-running the dispatch workflow is idempotent (byte-stable
within tolerance).

**Docs updates.** CHANGELOG entry; ADR-023 amendment; maintainer ticks `private/TODO.md`.

**Risks & open questions.** Two-step landing (spec first, baselines after merge) leaves the gate
red in between — use the same bootstrap-from-CI-actuals pattern as `562a1c3` to land specs +
baselines in one PR if preferred. How many states to double is a judgment call — start minimal;
adding more later is cheap. **Size: S** (after B).

---

## 6. Item D — Extended e2e coverage (ADR-022 deferred follow-on)

**Objective.** Cover the explicitly deferred e2e areas: Directory navigation, error states, and
keyboard-only sequences — the last of which also backs the UX-contract invariant ("keyboard path"
in the Definition of Done).

**Current state.** `web/e2e/` covers smoke, the budgeted flows (payment ≤3 interactions, 0-click
owner balances, ≤1-click uncleared, ≤2-click reconcile), M3 ledger, M4 banking, M5 reports, M6
runs, M7 onboarding, and a11y. Deferred per ADR-022: Directory navigation depth, error-state
rendering, keyboard-only sequences.

**Design / approach.** Three new specs, reusing `helpers.ts` seed/session plumbing and running in
the existing CI `e2e` job (serial, single worker — keep each spec tight):

- `directory-navigation.spec.ts` — owner → property → unit → tenant → lease drill-through, list
  filtering/search, ⌘K palette jump to each entity type, breadcrumb/back integrity.
- `error-states.spec.ts` — API-error and empty-state rendering: force failures via
  `page.route()` interception (500 → error boundary/toast; empty org fixtures → designed empty
  states). Assert on the designed states, not raw text, and that no client-side financial math
  fills the gap (the SPA renders server figures only).
- `keyboard-only.spec.ts` — complete one budgeted flow end-to-end with keyboard alone (record a
  tenant payment via ⌘K → composer → submit), plus focus-visible assertions (the ledger focus-ring
  rekey from `4d19637` shows this area is fragile).

Interaction-budget assertions for the keyboard path should reuse the counting approach already in
`budgeted-flows.spec.ts`. **ADR:** none — test-only.

**Tasks.**

1. Add the three spec files under `web/e2e/` (names above), reusing `helpers.ts`; extend helpers
   with a `routeFail(page, urlPattern)` utility for the error spec.
2. If empty-state coverage needs an empty org, extend the e2e seed path (the CI `e2e` job's
   demo+cutover seeding step in `.github/workflows/ci.yml` and the seeder entry point in
   `src/LeaseBook.Web`) with an `--org empty` variant — **seeder change touches no golden figures**
   (seed data is sacred; an additional empty org adds nothing to the demo org's numbers). Guarded
   non-Production only, like `ab75aa3`.
3. Fix whatever the keyboard spec flushes out (likely small `tabIndex`/focus-order fixes in
   `web/src/components/` — design-token classes only, no color-only status).

**Testing.** New specs green locally and in CI; a11y + visual gates unaffected; if any budgeted UX
flow's implementation changes as a fallout fix, the corresponding existing e2e must still pass
(UX-contract invariant). `npm run lint` / `typecheck` / `test` for any SPA fixes; full
`dotnet test LeaseBook.slnx` if the seeder is touched.

**Docs updates.** CHANGELOG; ADR-022 Consequences amendment (deferred list shrinks); README testing
section only if a new npm script is added (avoid — plain spec files need none); maintainer ticks
`private/TODO.md`.

**Risks & open questions.** CI e2e job runtime growth (ADR-022 flags it as the slowest job) — keep
the three specs lean and serial-friendly; if runtime becomes the constraint, propose sharding in a
future spec, not here. `page.route()` interception must not leak into other specs (scope per test).
**Size: M.**

---

## 7. Item E — Prod database private networking (Bicep)

**Objective.** Author the missing VNet-integration layer so a prod deploy can actually reach the
`publicNetworkAccess: 'Disabled'` PostgreSQL Flexible Server — the gap `infra/README.md`
§Production networking explicitly leaves open.

**Current state.**

- `infra/modules/database.bicep` disables public access for prod and notes prod must wire
  `network.delegatedSubnetResourceId` + a private DNS zone; no `infra/modules/network.bicep`
  exists; `infra/env/prod.bicepparam` carries no VNet parameters.
- `infra/modules/containerapp.bicep` creates the Container Apps environment without VNet
  integration (fine for dev; prod needs the environment on the VNet to reach the private DB).
- Deploy workflows run EF migrations from GitHub-hosted runners — that path **cannot** reach a
  private-only prod DB.

**Design / approach.** (Author with the `azure-infrastructure` agent; validate with
`az bicep build` + `what-if`, which need no live resources beyond a subscription for what-if.)

- New `infra/modules/network.bicep`: VNet + two subnets — one delegated to
  `Microsoft.DBforPostgreSQL/flexibleServers`, one for the Container Apps environment
  (`Microsoft.App/environments` infrastructure subnet) — plus a
  `privatelink.postgres.database.azure.com` private DNS zone and VNet link.
- Thread outputs through `infra/main.bicep` behind an `enablePrivateNetworking` boolean parameter
  (dev: `false`, unchanged behavior; prod: `true`), into `database.bicep`
  (`delegatedSubnetResourceId`, `privateDnsZoneArmResourceId`) and `containerapp.bicep`
  (`vnetConfiguration.infrastructureSubnetId`).
- **Prod migration path decision** (load-bearing — record as **ADR-024**): GitHub-hosted runners
  can't reach the private DB, so prod migrations must move off the runner. Preferred option,
  consistent with the existing "one-shot migrator job, never at app startup" rule: run the EF
  migration bundle as a **Container Apps Job** inside the VNet (the deploy workflow builds the
  existing `migrator`-target image and triggers the job via `az containerapp job start`), replacing
  `deploy-prod.yml`'s direct `dotnet ef database update` step. Alternatives (self-hosted runner in
  the VNet; temporary firewall exception) rejected in the ADR.
- Money/RLS invariants are untouched; the three-role model is unchanged — only connectivity moves.

**Tasks.**

1. `infra/modules/network.bicep` — VNet, delegated PG subnet, ACA infra subnet, private DNS zone +
   link; outputs.
2. `infra/main.bicep` — `enablePrivateNetworking` param; conditional module wiring.
3. `infra/modules/database.bicep` — accept + apply the two network parameters when enabled.
4. `infra/modules/containerapp.bicep` — optional `vnetConfiguration`; add a Container Apps **Job**
   resource for the migrator (image param), created in both envs (dev may keep using the workflow
   path until cutover).
5. `infra/env/prod.bicepparam` — enable + address-space parameters; leave `dev.bicepparam` as-is.
6. `.github/workflows/deploy-prod.yml` — replace the direct `dotnet ef database update` step with
   image build/push of the `migrator` target + `az containerapp job start --name lb-prod-migrate …`
   and a wait-for-completion check.
7. `docs/adr/ADR-024-prod-private-networking-and-migration-job.md` (+ index row in
   `docs/adr/README.md`).
8. `infra/README.md` — replace the §Production networking caveat with the implemented design;
   document new parameters.

**Testing.** `az bicep build --file infra/main.bicep` clean (CI-friendly, no credentials);
`az deployment sub what-if` for both env params **(operator-run)**; actual apply is Item F. Workflow
lint via `actionlint` if available locally; otherwise rely on review — the workflow can't be
exercised until Item F.

**Docs updates.** ADR-024; `infra/README.md`; CHANGELOG; CLAUDE.md only if commands change (they
don't); maintainer ticks `private/TODO.md`.

**Risks & open questions.** ACA environment VNet integration constrains subnet sizing (minimum /23
for workload profiles — verify against current Azure docs at authoring time); private DNS zone
naming must match the Flexible Server requirement exactly; the migrator-job image already exists as
a Dockerfile target (`migrator` — confirm the target name in the root `Dockerfile` before wiring).
Address spaces need operator confirmation (no committed constraint). **Size: M.**

---

## 8. Item F — Live environment enablement + first deploys (operator-gated)

**Objective.** Turn the authored infra + workflows into a running dev environment, then prod. This
is the "operator-gated remainder of M0" CLAUDE.md tracks, now scheduled by M8.

**Current state.** Everything is authored: Bicep (`infra/`), role bootstrap doc
(`infra/db/azure-bootstrap.md`, `infra/db/bootstrap.sql`), deploy workflows (`deploy-dev.yml`
auto-runs after CI on main but fails at Azure login until configured; `deploy-prod.yml` is
dispatch-only behind the `prod` environment's required reviewers). Nothing is live.

**Design / approach.** Checklist, not code. Steps marked **[OP]** need operator Azure/GitHub-org
access; **[ENG]** items are engineering support that can be done from a PR.

**Tasks (dev leg).**

1. **[OP]** Create the Entra app registration + OIDC federated credentials for the repo's `dev`
   environment; create the GitHub `dev` environment; set secrets `AZURE_CLIENT_ID`,
   `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, and vars `ACR_NAME`, `APP_NAME`, `RESOURCE_GROUP`
   (names per `infra/README.md` convention: `lbdevacr`, `lb-dev-app`, `lb-dev-rg`).
2. **[OP]** `az deployment sub create … --parameters infra/env/dev.bicepparam` (commands in
   `infra/README.md`).
3. **[OP]** Bootstrap the three Postgres roles per `infra/db/azure-bootstrap.md`; store the app +
   migrator connection strings as Key Vault secrets per the secrets contract table in
   `infra/README.md`; set the workflow's `MIGRATIONS_CONNECTION_STRING` secret.
4. **[OP]** Dispatch `deploy-dev.yml`; verify image push, migration step, Container App revision,
   `/api/health`.
5. **[ENG]** First-deploy verification PR if anything surfaces (e.g., health-check path, container
   port, Key Vault reference names) — fixes land in `infra/` or `Dockerfile`, never as portal-only
   drift.
6. **[ENG]** Seed a demo org in dev **only if** the operator wants a demo environment — note the
   seeder is guarded non-Production (`ab75aa3`); prod must never be seeded.

**Tasks (prod leg — after Item E merges).**

1. **[OP]** Same OIDC/environment setup for `prod` + required reviewers on the GitHub `prod`
   environment.
2. **[OP]** Deploy `infra/env/prod.bicepparam` (now with private networking); bootstrap roles via a
   VNet-reachable path (the migrator job or a temporary jump host — per ADR-024).
3. **[OP]** Promote a dev-built image via `deploy-prod.yml` (`image_tag` input).

**Testing.** The deployment *is* the test: green `deploy-dev.yml` run end-to-end; `/api/health`
200; a smoke pass of login + dashboard against dev; `check-invariants` run against the dev DB after
the first seeded/demo data exists. No repo test gates apply to [OP] steps.

**Docs updates.** After the first successful dev deploy: CLAUDE.md "operator-gated remainder"
paragraph shrinks accordingly; CHANGELOG `### Added` (live dev environment — engineering-visible
wiring only, no business detail); `infra/README.md` "gated on operator access" preamble updated.
Maintainer updates `private/deployTODO`-equivalent sections in `private/TODO.md`.

**Risks & open questions.** OIDC subject claims must match the workflows' `environment:` names
exactly; ACR name global uniqueness (`lbdevacr` may be taken — the `bicepparam` should then change
in-repo, not ad hoc); dev DB is public+firewalled by design — acceptable for dev only. **Size: M
(operator time), S (engineering).**

---

## 9. Item G — First PITR restore drill (operator-gated)

**Objective.** Execute the first point-in-time-restore drill against the dev environment and
complete `docs/runbooks/restore.md` (its explicit `TODO (first drill)`).

**Current state.** `docs/runbooks/restore.md` is a skeleton: PITR via
`az postgres flexible-server restore`, verify as `leasebook_ops`, run the invariant suite against
the restored DB, cut over Key Vault connection strings, decommission. Retention: dev 7 days, prod
35, geo-redundant in prod (`infra/modules/database.bicep`).

**Design / approach.** Drill on **dev** after Item F's dev leg. The one engineering-authorable gap:
the runbook says "run the trust-accounting invariant suite against the restored database before
cutover" — make that a one-command reality: the existing
`dotnet run --project src/LeaseBook.Web -- check-invariants --all` already accepts a connection
string via configuration; verify it can target an arbitrary DB via `ConnectionStrings__Default` env
override and document the exact invocation in the runbook. No ADR.

**Tasks.**

1. **[ENG]** Verify/document the `check-invariants --all` invocation against a non-default
   connection string (env-var override) — add the exact command to `docs/runbooks/restore.md` step 3.
2. **[OP]** Execute the drill on dev: pick a timestamp, restore to `lb-dev-pg-restored`, run the
   invariant suite, practice the Key Vault repoint (then revert), delete the restored server.
3. **[ENG]** Fill in the runbook's `TODO (first drill)` — actual duration, observed data-loss
   window, manual steps discovered; remove the "Skeleton procedure" caveat.

**Testing.** The drill itself; invariant suite green against the restored DB ("a restore that
doesn't reconcile to the cent is not a successful restore").

**Docs updates.** `docs/runbooks/restore.md` completion; CHANGELOG (`### Added`: restore runbook
validated by first drill); CLAUDE.md operator-gated list shrinks; maintainer ticks
`private/TODO.md` (M8 schedules this drill).

**Risks & open questions.** Dev retention is 7 days — schedule the drill within a week of having
meaningful data. Restored-server cost — decommission promptly. **Size: S (engineering), S–M
(operator).**

---

## 10. Item H — Payments module (Phase 2 — outline only, blocked on private scope)

**Objective.** Replace the `LeaseBook.Modules.Payments` shell with the Phase-2 payments capability.

**Current state.** `src/LeaseBook.Modules.Payments/` contains `ModuleMarker.cs`, an empty
`Contracts/`, and the csproj — a compile-time placeholder wired into the module-boundary
architecture tests. Tenant payments today are recorded manually through the M3 ledger action hub
(`TenantPaymentReceived` posting templates). No committed doc defines Phase-2 payments scope;
CLAUDE.md assigns Payments to "M5–M8 / Phase 2" and the PRD (private) is the scope authority.

**Blocked:** do not design against a guess (Ground Rules). Before any build, the maintainer must
supply from `private/LeaseBook_PRD_v1.0.md` / `private/TODO.md`: processor vs ACH-file vs
record-only scope, payer surface (portal? ADR-003 reserved portal sub-org scoping), fee handling,
and refund/NSF treatment — each is load-bearing for the posting design.

**Constraints that will bind any design (committed, non-negotiable):**

- Module boundary (ADR-007): Payments reads other modules only through consumer-owned batch ports
  in `src/LeaseBook.Modules.Payments/Contracts/`, host adapters delegating via `ISender`.
- All money movement posts through **posting templates** (ADR-006) on the append-only journal —
  a processor webhook never writes ledger state directly; it raises a business event
  (`PaymentInitiated` → `PaymentSettled` / `PaymentFailed` / `PaymentReturned`) whose templates
  post per basis. NSF/chargeback = linked reversal + fee event, never an update.
- New tables (payment intents, processor refs, webhook inbox) are org-scoped through the migrations
  RLS helper; webhook processing is a background path → must establish org context explicitly and
  fail closed. Idempotent webhook inbox (unique processor event id) — processors redeliver.
- Money stays `decimal` / `NUMERIC(14,2)`; processor amounts in minor units are converted at the
  edge with an exactness check.
- Trust-accounting isolation: processor fees charged to the PM are PM-expense postings, never
  owner-ledger reachable; deposits collected online remain liabilities until applied (ADR-011).
- Expected new ADRs: processor selection + webhook/idempotency model; settlement/clearing account
  design (an undeposited-funds clearing account mirrors the M7 migration-clearing pattern:
  nets-to-zero invariant).

**Rough shape once unblocked (indicative):** ADR + spec (S) → schema/migrations + posting templates
+ invariant tests (M) → webhook inbox + processor adapter behind an `IPaymentProcessor` seam with a
fake for tests (M) → SPA surfaces + e2e (M). **Size: L overall.**

---

## 11. Explicit non-items

- **TS6 major upgrade** — automated watch (`.github/workflows/ts6-unblock-watch.yml`) files an
  issue when `openapi-typescript` unblocks; no scheduled work.
- **`JournalLineConfiguration.cs:58` "§1 of TODO" comment** — a citation of `private/TODO.md`, not
  an open task; leave as-is.
- **Percy/Chromatic evaluation** — only on ADR-023's revisit trigger (visual flake beyond the 2%
  tolerance), which has not fired.
