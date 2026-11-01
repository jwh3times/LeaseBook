# Ship

## Fast checks

Together they cover what the required checks run that can run here. They need `npm ci` in `web/`
and the .NET SDK from `global.json`; `npm run api:generate` also installs `web/codegen/`.

- repo root, only when `src/`, `tests/`, `Directory.*.props`, `LeaseBook.slnx` or `.editorconfig`
  changed: `dotnet format --verify-no-changes --exclude src/LeaseBook.Web/Migrations`
  (**Backend (build + test)**), fixed by `dotnet format --exclude src/LeaseBook.Web/Migrations`
- `web/`, only when `web/` changed: `npm run format:check` (**Web (lint + test + build)**), fixed
  by `npm run format`
- `web/`: `npm run docs:check` (**Web**: Prettier, markdownlint and cspell over the Markdown,
  `scripts/*.mjs`, `cspell.json` and `.markdownlint-cli2.jsonc`, then the docs-policy tests,
  `scripts/check-docs-policy.mjs` and `scripts/check-docs.mjs`), a formatting failure fixed by `npm run docs:format`
- repo root: `node scripts/sync-agents.mjs --check` (**Web**), fixed by `npm run docs:format` from
  `web/` then `node scripts/sync-agents.mjs`, committing `.claude/skills/` and `.codex/agents/`
- repo root: `node scripts/check-skills.mjs` (**Web**)
- repo root: `node scripts/lint-agent-neutrality.mjs` (**Web**)
- repo root: `node --test scripts/*.test.mjs` (**Web**'s script tests, a few seconds)
- repo root: `node scripts/scenario-catalogue.mjs check` (**Web**; needs `web/node_modules`)
- `web/`, only when `web/` changed: `npm run lint`, then `npm run build` (**Web**; its `tsc -b` is
  the typecheck)
- `web/`, only when `web/` changed: `npm run test:coverage` (**Web**'s unit suite, about 13
  seconds)
- repo root, only when `src/`, `tests/`, `Directory.*.props` or `LeaseBook.slnx` changed:
  `dotnet build -c Debug` (**Backend**); then, when `src/` changed, `dotnet tool restore` and
  `dotnet ef migrations has-pending-model-changes --project src/LeaseBook.Web --context AppDbContext --no-build --configuration Debug`
  (**Backend**'s missing-migration step)
- `web/`, only when `src/` or `web/codegen/` changed:
  `npm run api:generate && test -z "$(git status --porcelain -- src/api/generated)"` (**API client
  matches OpenAPI contract**), fixed by committing the regenerated `web/src/api/generated/`; then,
  from the repo root,
  `node scripts/scenario-catalogue.mjs check --no-tags --openapi src/LeaseBook.Web/obj/openapi/LeaseBook.Web.json`

Left to CI: the backend tests (`dotnet test` needs Docker for Testcontainers), **Migrations apply to
a blank DB**, **Load fixture seeds + pin holds**, **E2E (Playwright + a11y)** and **E2E (Stripe
stand-in)** (Postgres and a seeded host), **Container build + full-stack smoke**, **Secret scan**
(gitleaks is not installed here), and the ruleset's CodeQL, code-quality and Copilot review.
**CHANGELOG [Unreleased] updated** is the `Changelog check` in `releases.md`.

## Hook: before PR

1. When the linked issue is on the board with `Track` or `Gate` unset, warn (`Board fields` in
   `issue-tracker.md`; `gh project item-list <number> --owner <owner> --format json --limit 500`,
   with the `Board` setting's owner and number).
2. Every `docs/` Markdown file the branch or the working tree changes, except the ADR records,
   `docs/adr/template.md`, the documents in `reviewDateExempt` and the skill settings files in
   `skillSettings` (both `scripts/check-docs-policy.mjs`), carries today's date in
   `- **Last reviewed:**`. The local `npm run docs:check` cannot see a stale date until the change
   is committed; CI's can. Fix the date, then rerun `npm run docs:check`.
3. An accounting-adjacent diff (`src/LeaseBook.Modules.Accounting/`, posting templates,
   `src/LeaseBook.Web/Migrations/`, `seed/`): report that the invariant, property-based and
   golden-file suites run only in CI here and were due during development (Definition of Done), and
   that a changed seed figure needs its golden tests changed in the same diff.
4. A product-source change with no user-visible effect and no `Unreleased` entry: add the
   `skip-changelog` label once the PR exists (`gh pr edit <n> --add-label skip-changelog`).
5. Fill the template: the link line in its `Closes #` slot (deleted for exempt work); **What & why**;
   tick a **Definition of Done** item only on evidence from the diff or the fast checks;
   **Verification** with the fast checks run and the checks left to CI.
6. Report that CodeQL (medium-or-higher alerts), code quality and the Copilot review also run on the
   PR and can block it, and that the ruleset requires the branch to be up to date with `main`.
