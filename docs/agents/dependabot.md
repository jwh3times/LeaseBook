# Dependabot

- **Audience:** Coding agents and maintainers
- **Status:** Living policy
- **Owner:** Maintainers
- **Last reviewed:** 2026-11-22

What reviewing a Dependabot pull request takes here, read by the `dependabot` skill in a session
and in the scheduled review. These facts apply to this repository only. How a dependency PR merges
is in [`merging.md`](merging.md#dependency-pull-requests).

## Stack

- .NET 10 modular monolith, `LeaseBook.slnx`. `global.json` pins SDK `10.0.100` with
  `rollForward: latestFeature`; CI's `actions/setup-dotnet` installs `10.0.x`. Package versions are
  central in `Directory.Packages.props`, local tools in `.config/dotnet-tools.json`, and
  `Directory.Build.props` sets `TreatWarningsAsErrors`.
- The SPA in `web/`: Vite, React, TypeScript 7, oxlint, Vitest, Playwright. The API client generator
  sits apart in `web/codegen/`, with its own lockfile and TypeScript 5 (ADR-030). Node 26: `.nvmrc`,
  `web/.nvmrc`, `engines`, and CI's `actions/setup-node` `'26'`.
- Images: the `Dockerfile` builds from `node:26-bookworm-slim`, `mcr.microsoft.com/dotnet/sdk:10.0`,
  `mcr.microsoft.com/dotnet/aspnet:10.0` and `aspnet:10.0-noble-chiseled`, each pinned by digest.
  `docker-compose.yml` runs `postgres:18` and `dpage/pgadmin4`.
- Production is Azure Database for PostgreSQL Flexible Server, `version: '18'` in
  `infra/modules/database.bicep`. Nothing is deployed: `deploy-dev.yml` runs only once
  `vars.ACR_NAME` is set, and enabling the deploys is operator-gated work (`AGENTS.md`).
- Workflows: `.github/workflows/` holds `ci.yml`, `coverage.yml`, `mutation.yml`, `docs-links.yml`,
  `changelog.yml`, `version.yml`, `codegen-unblock-watch.yml`, `update-visual-baselines.yml`,
  `deploy-dev.yml` and `deploy-prod.yml`.
- `.github/dependabot.yml` updates `nuget`, `docker`, `docker-compose` and `github-actions` at `/`,
  `npm` at `/web` and, weekly, at `/web/codegen`. Minor and patch bumps are grouped per ecosystem,
  except Stripe.net, which gets its own PR. Majors open one PR each, except the peer-locked families
  (`xunit.v3*`; `react`; `@tanstack/*`; `vite`, `vitest` and `@vitejs/plugin-react`;
  `@testing-library/*`; `oxlint`), which travel together.

## Review triggers

- **Stripe.net, any bump.** A release can move the API version the SDK sends, and CI goes red by
  design until a maintainer records a probe run at the new version. 53.1.0 to 53.2.0, a minor, did
  (#806).
- **Microsoft.AspNetCore.OpenApi and Microsoft.Extensions.ApiDescription.Server, any bump.** Both
  depend on the `Microsoft.OpenApi` pin in `Directory.Packages.props`. A patch of the first failed
  restore with `NU1109` (#764).
- **The other framework packages, any bump**: `Microsoft.AspNetCore.*`,
  `Microsoft.EntityFrameworkCore*`, `Microsoft.Extensions.*`,
  `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions` and `dotnet-ef`. They
  version with the runtime, and their majors are held back by an ignore rule until the .NET 11
  migration (#761).
- **The `dotnet/*` base images, any bump, digests included.** Their majors are ignored, so a PR is
  normally a digest refresh of the `10.0` tags; read its diff for a tag change.
- **`postgres`, any bump.** The compose tag is the bare major, so a bump is a major (see
  `Grounding facts`).
- **`vite`, `vitest`, `react`, `react-dom`, `typescript` and `@tanstack/react-query`, any
  bump.** The SPA's build, type-check and test toolchain.
- **`oxlint`, `oxlint-tsgolint` and `cspell`, any bump.** They are CI gates, so a new rule or
  dictionary entry can fail on content merged since the PR opened.

## Resolution

Under Node 26 (`.nvmrc`) and the .NET SDK `global.json` names, the installs CI runs:

- NuGet (`Directory.Packages.props`, a `.csproj`, `.config/dotnet-tools.json`), from the root:
  `dotnet restore`, then `dotnet build -c Release --no-restore`. A tools-manifest bump also needs
  `dotnet tool restore`.
- npm in `web/`, from `web/`: `npm ci`.
- npm in `web/codegen/`, from `web/codegen/`: `npm ci`. CI runs it inside `npm run api:generate`
  (`npm --prefix codegen ci`) in the `API client matches OpenAPI contract` job.

## Where to look

- The `postgres:18` pins. Dependabot's `docker-compose` update moves only `docker-compose.yml`, and
  a major leaves the rest behind: the service containers in `ci.yml` (four jobs), `coverage.yml` and
  `update-visual-baselines.yml`; `PostgresFixture.cs` in `tests/LeaseBook.Tests.Common/`
  (Testcontainers); `infra/db/Dockerfile` (`postgres:18-bookworm`, digest-pinned, which no
  Dependabot entry scans); and `version` in `infra/modules/database.bicep`. The compose data volume
  is mounted at `/var/lib/postgresql`.
- The Node 26 pins: the `Dockerfile` web stage, `.nvmrc`, `web/.nvmrc`, `engines` in
  `package.json`, `web/package.json` and `web/codegen/package.json`, and `node-version` in the
  workflows.
- The .NET 10 pins: `global.json`, `dotnet-version` in the workflows, the `Dockerfile`
  `mcr.microsoft.com/dotnet` tags, `.config/dotnet-tools.json`, and the ignore rules in
  `.github/dependabot.yml`.
- The Stripe API version: `API_VERSION` in `scripts/stripe-probe.mjs`, which
  `scripts/stripe-stand-in.mjs` also answers for; `PinnedVersionRun` in
  `tests/LeaseBook.Tests.Integration/StripeSandboxProcessorTests.cs`; the recorded runs under
  `tests/fixtures/stripe/`.
- The comment above the `Microsoft.OpenApi` pin in `Directory.Packages.props`.
- The SPA's tool configs: `web/vite.config.ts`, `web/tsconfig*.json`, `web/.oxlintrc.json`,
  `web/playwright.config.ts`, `web/.prettierrc.json`, and `cspell.json` at the root.

## Grounding facts

- A PostgreSQL major, of the `postgres` image or the Flexible Server, is an inherent break: a newer
  major refuses to start on a data directory an older major created. It is always NEEDS EFFORT and
  needs no external confirmation, but ground the steps in this repository. CI's jobs start from
  empty databases, so a green CI does not show that existing data survives.
- A .NET major moves together with `global.json`, every `setup-dotnet` `dotnet-version`, the
  `Dockerfile` `dotnet` tags and `dotnet-ef` in `.config/dotnet-tools.json`. The `global.json` roll
  forward (`latestFeature`) does not cross a major, so a lone SDK image or runtime-versioned package
  major fails restore or build. Until #761 they are ignored in `.github/dependabot.yml`.
- `TreatWarningsAsErrors` is on (`Directory.Build.props`): an obsoleted API, or a restore warning
  such as `NU1608`, fails the build.
- `web/codegen/` stays on TypeScript 5 on purpose, and its TypeScript major is ignored (ADR-030);
  the SPA's own `typescript` is `~7.0`.
- No bump reaches a deployed environment: nothing is deployed yet (`Stack`).

## Playbook

- **A Stripe.net bump that moves the API version** (confirmed in the repository and in the verdict
  on #806). It goes red by design in two checks: `Backend (build + test)`, where
  `StripeSandboxProcessorTests.A_card_payment_is_sent_as_the_probe_sent_it_and_nothing_more`
  expects the `Stripe-Version` header of the pinned run, and `E2E (Stripe stand-in)`, where the
  stand-in refuses any version but `API_VERSION` and the host fails to start. The fix is not
  mechanical and not a review's to make: it needs a maintainer's Stripe sandbox. Follow "Raise the
  pinned API version" in [`stripe-sandbox-probe.md`](../runbooks/stripe-sandbox-probe.md): set
  Stripe.net and `API_VERSION` on a branch, `node scripts/stripe-probe.mjs run`, then
  `node scripts/stripe-probe.mjs scrub stripe-probe.local/<run>`, point the tests at the new run,
  and `node --test scripts/stripe-probe.test.mjs`. That test fails when no committed run was
  recorded at `API_VERSION`, and names each field the new run lost that an older replayed run
  holds. Editing `API_VERSION` alone does not pass it. A Stripe.net release that keeps the version
  is routine.
- **Microsoft.AspNetCore.OpenApi or Microsoft.Extensions.ApiDescription.Server** (confirmed in the
  verdict on #764 and the fix that merged with it). When the new release's floor for
  `Microsoft.OpenApi` is above the pin, restore fails:
  `NU1109: Detected package downgrade: Microsoft.OpenApi`. Raise the `Microsoft.OpenApi`
  `PackageVersion` in `Directory.Packages.props` to that floor, matching what the
  `ApiDescription.Server` tool loads (the comment there). Gates: the NuGet `Resolution`, then
  `npm run api:generate` from `web/` with no change under `web/src/api/generated`, which is the
  `API client matches OpenAPI contract` job.
- **A PostgreSQL major (18 to N).** Bumping the tag does not migrate data. Move every pin in
  `Where to look` together. Locally, `./scripts/dev.ps1 reset-db` wipes the volume and
  re-bootstraps; then apply the migrations and seed the demo org with the commands under Database
  in `AGENTS.md`. Use `pg_dump` and a restore if local data must survive. The postgres 18 images
  keep `PGDATA` under `/var/lib/postgresql/<major>/docker`, so the data directory changes across a
  major and a clean reset is simplest. The Flexible Server's `version` in `database.bicep` waits
  until something is deployed. Verify: the app boots, the migrations apply, and
  `check-invariants` passes on the demo org.
- **A .NET major, once #761 lifts the ignores**, and only for a breaking change step 5 found: move
  the pins in `Where to look` together, then build and run the whole test suite with the commands
  in `AGENTS.md`, the invariant, property-based and golden-file suites included. Cite the specific
  change.
- **A build-critical npm major in `web/`**: from `web/`, `npm run build` (which runs `tsc -b`),
  `npm run lint` and `npm run test`. Cite the specific change.
- **A `@hey-api/*` or `typescript` bump in `web/codegen/`**: `npm run api:generate` from `web/`, and
  read the diff under `web/src/api/generated`.
