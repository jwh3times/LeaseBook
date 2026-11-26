# Local runs

- **Audience:** Coding agents and maintainers
- **Status:** Living policy
- **Owner:** Maintainers
- **Last reviewed:** 2026-11-26

How to run builds, tests and gates on a development machine without a false green. The commands
are in `AGENTS.md` and [`docs/runbooks/local-dev.md`](../runbooks/local-dev.md); this file holds
their traps.

## Exit codes

- **A pipe hides the exit code.** `npm run docs:check | tail -3 && echo OK` prints OK on a failed
  run, and `gh run watch <id> --exit-status | tail` exits 0 on a failed workflow. Filtering is the
  same bug: `npm run test | grep "Tests "` showed `248 passed` on a run that exited 1, because
  Vitest prints an unhandled rejection outside the summary. Run a gate bare, or append
  `; echo EXIT=$?`.
- **Never chain a commit after a test run with `;` or a newline.** A commit went in with 1 of 496
  red, and a crashed script left an empty file that the next line's `gh pr edit --body-file` still
  published, wiping a PR body. Chain with `&&` or `set -e`, and gate a publish on `test -s <file>`.
- **Make the up-to-date check stop the push.** Printing `behind` gates nothing: run
  `git fetch && git merge-base --is-ancestor origin/main HEAD || exit 1` right before pushing.

## .NET test runs

- **`dotnet test` can report `Zero tests ran` and exit 5 on a project whose tests all pass**, with
  no other diagnostic. Run the built test host directly:
  `tests/<Project>/bin/Debug/net10.0/<Project>.exe`. It takes xUnit console arguments (`-class
<FQN>`, `-method <FQN.Method>`), not `dotnet test`'s, and the two AND together.
- **`--filter-class` plus `--filter-method` match the intersection**, so a class and a method from
  another class run nothing and print `failed: 0`. Run them as separate invocations and always
  print the total beside the failures. `--filter` matches class and namespace names, never file
  names.
- **A full solution run gets killed for low memory** with Docker Desktop up. Run the test projects
  one at a time; for an accounting change, run its golden and seeder classes in the foreground with
  class filters, or say plainly that CI is the first full Integration run.
- **A running `dotnet run` host locks `LeaseBook.Web.exe`**, so the next build fails with
  MSB3027/MSB3021, and a test run after that failure silently uses the stale binary.
- **`dotnet ef` fails with `NETSDK1004` on a fresh worktree** until one `dotnet build`: the error
  names restore, not the missing build.
- **`dotnet ef migrations remove` needs a live database** and leaves the files when it fails, so a
  following `migrations add` diffs against the old snapshot and duplicates the change. To rename an
  unapplied migration, delete its `.cs` and `.Designer.cs`, rebuild, and confirm with
  `has-pending-model-changes`.
- **`dotnet format` reflows new test files**, so a scripted edit anchored on pre-format text stops
  matching. Re-read after formatting.

### Local Stryker on Windows

- Analysis fails (`MSB4276`, SDK not found) because it picks the Visual Studio Build Tools MSBuild.
  Pass `--msbuild-path "C:/Program Files/dotnet/sdk/<dotnet --version>/MSBuild.dll"`, running
  `dotnet stryker --config-file stryker-config.json` from the test project, since
  `scripts/mutation.mjs` doesn't pass the flag through.
- A `--mutate "**/File.cs{a..b}"` span is character offsets, not lines; a line range selects the
  usings and tests 0 mutants.
- It can restore an old dll into the test project's `bin/` from a `*.dll.stryker-unchanged` backup
  left by an earlier run. Afterwards, delete the backup and build with `--no-incremental` before
  trusting a result.

## End-to-end runs

Use `npm run e2e:fresh` ([local-dev](../runbooks/local-dev.md#end-to-end-playwright--a11y)).

- **Parallel worktrees share the fixed e2e and perf ports** (5080, 5373). Serialize them with an
  atomic `mkdir` lock directory, and give each its own throwaway Postgres container on a free port,
  never the main checkout's `leasebook-db`.
- **The payment fixture isn't reset between runs**, so a spec that counts rows page-wide fails the
  second time: scope every assertion to the payment's own reference.
- **Every test fails at launch after a Playwright version bump**, because the browser build the new
  version needs isn't installed; the one-time install in the runbook was for the old version. Run
  `npx playwright install chromium` from `web/` again.
- **A stale e2e template can fail a few specs that the code doesn't break.** A full
  `npm run e2e:fresh` run once failed only the owner-portal statement-PDF tests, with the statement
  PDF answering 503, and all four passed after a rebuild; why the template was stale wasn't
  established. The template rebuilds itself only when a migration, a seeder or the bootstrap SQL
  changes. Before debugging a red that CI doesn't show, run
  `node scripts/e2e-db.mjs reset --rebuild` from the repository root, which drops and rebuilds
  `leasebook_e2e_template` (bootstrap, migrate, seed) whatever its fingerprint says and recreates
  the copy from it, then run again.
- **Grep the specs before adding a control.** Playwright's `getByLabel` and `getByRole` names match
  substrings, case-insensitively, so a new "Finalize May 2026 again" button broke an unrelated
  `name: 'Finalize'` click. Run `grep -rn "name: '<word>'" web/e2e` for each word of a new name,
  and run every spec that visits the screen, not only the new one, during `/implement`.

## The docs gate

- **`npm run format` covers `web/` only.** Root Markdown, `docs/`, `infra/`, `.github/` and
  `.claude/agents/` need `npm run docs:format`, which must run before `node scripts/sync-agents.mjs`,
  or the mirror is generated from unformatted source.
- **Run `docs:check` after committing.** Its review-date rule compares a doc's `Last reviewed:`
  header with the file's last commit date, so on a dirty tree it always passes. Editing any doc with
  that header, runbooks included, means bumping it.
- **It can't pass in a shallow clone**, where every file's last commit is the graft point.
  `git fetch --unshallow` first, and don't "fix" the headers it flags.
- **Root Markdown scratch files are inventoried**, even dot-prefixed ones like `.pr-302.md`. Draft PR
  and issue bodies outside the checkout.
- **`.agents/skills/` is outside the gate and every formatter** (`web/.prettierignore`, and the
  globs in `web/package.json` and `cspell.json`): `scripts/check-skills.mjs` fails an installed copy
  that differs from `skills-lock.json`, so never format one.
- **cspell scans Markdown only**: reword a one-off coinage rather than growing `cspell.json`.
- **A new table row wider than the columns makes Prettier reflow the whole table.** Pad the new row
  to the existing widths.
- **Prettier is unstable on code fences nested in list items.**
- The two `fatal: not a git repository` lines in its output come from its own tests' fixtures;
  "Documentation policy check passed." is the verdict.

### ADR edits

- **An `Amended by:` header requires the matching `docs/adr/README.md` status**,
  `Accepted (amended by ADR-0NN)`, or the gate fails. Use it when a different ADR changed this one's
  decision.
- **A dated `### YYYY-MM-DD amendment` section** records an ADR changing its own mind, with no index
  change.
- **When no decision changed** (the prose stopped describing the mechanism, or an enumeration gains
  an entry it always implied), edit the ADR in place, with no marker and no index change: the index
  date must keep matching the ADR's own `Date:`.

## The OpenAPI build

- **It runs as Production.** The schema-drift job runs `Program` up to `app.Run()` with no
  environment, so `appsettings.Production.json` applies with no connection string. Anything startup
  touches that needs a dependency must be left out in the OpenAPI build mode
  (`LEASEBOOK_OPENAPI_BUILD`, `HostProcessMode.OpenApiBuild`, ADR-042), or the gate fails with an
  exception, not a diff. After touching startup, reproduce with
  `LEASEBOOK_OPENAPI_BUILD=1 dotnet build src/LeaseBook.Web/LeaseBook.Web.csproj -c Debug -p:OpenApiGenerateDocumentsOnBuild=true`:
  `dotnet build` and `dotnet test` can't see it.
- **The document's path order depends on the environment.** `scripts/emit-openapi.mjs` pins
  Production; emitting by hand in a shell where `ASPNETCORE_ENVIRONMENT=Development` persists
  produces a reordering the drift gate rejects.
- **Generation is incremental on a cache**, `obj/LeaseBook.Web.OpenApiFiles.cache`, so a warm build
  writes no document. Delete the cache and the document to force it.

## CI workflows

- **`ci.yml` lists script tests by name.** A new `scripts/*.test.mjs` runs in CI only once a step
  names it. `scripts/script-tests-listed.test.mjs` fails on a file that no workflow or
  `web/package.json` script names, but it reads names only: it can't prove the naming step runs in a
  required job.
- **A red Windows result in open-pr's fast checks is your branch's.** They pass on a Windows checkout
  now. Run Playwright from a script through `scripts/playwright-cli.mjs`;
  `scripts/stripe-sandbox-run.mjs` still doesn't run on Windows.
- **Replaying a gate in Docker from a linked worktree needs a clone**: a worktree's `.git` is a file
  pointing at a Windows path. `git clone --no-checkout . <scratch>/repo` and mount that.
- **`git show REV:path | grep -c $'\r'` lies on Windows.** Count committed CRs with
  `git cat-file blob REV:path | tr -cd '\r' | wc -c`.
- **A transitive vulnerable package fails the audit-as-error build** (NU1903). Pin it with a
  transitive `PackageVersion`; `CentralPackageTransitivePinningEnabled` is on.
- **Changed npm `overrides` don't apply on a plain `npm install`**: run `npm update <package>`.
- **`npm install-scripts … --dry-run` writes `package.json` anyway** (npm 12.0.2). Check `git diff`
  after any npm dry run.
- **lychee:** a connection reset from one host is a concurrency burst, not a bad link (the job sets
  `--max-concurrency 16`; retries default to 3). GitHub's `blob` pages 503 bots for hours, so the
  check remaps them to raw. Reproduce with `docker run lycheeverse/lychee:<pinned version>`, and
  probe a timing-out host with `curl -m 30` before suspecting the PR.

## A remote session

A cloud session has no .NET SDK and no Docker, and never sees `private/`. Only the web toolchain
verifies there (`npm ci`, `lint`, `typecheck`, `test`, `build`, `docs:check`). Plan backend work
around CI as the gate, write the guard so it is locally verifiable where possible, and say in the PR
that the proving suites ran only in CI.

## This Windows machine

- **Invariant globalization** is on in dev and in the image, so `CultureInfo.GetCultureInfo("en-US")`
  throws. Use `CultureInfo.InvariantCulture`, which formats the way a US document reads.
- **No IANA time zone resolves** on Windows under `InvariantGlobalization`, nor in the chiseled
  image ([dotnet-api](../../.claude/agents/dotnet-api.md)). Green Ubuntu CI proves nothing about it.
- **`gh` timing out on `dial tcp 10.0.0.1:443`** is a stale DNS cache: `ipconfig /flushdns`.
- **`ln -s` in Git Bash copies** instead of linking. Check with `test -L`.
- **Editors and scripts can add a BOM**, which fails `dotnet format` with `error CHARSET` at line 1
  of each touched file (`.editorconfig` sets `utf-8`, no BOM). Check with
  `head -c 3 <file> | xxd -p` (`efbbbf`), and strip it in binary mode; `dotnet format` only reports
  it.
- **Git Bash rewrites container paths**: set `MSYS_NO_PATHCONV=1` for `docker run -v … -w /app` and
  for `git show origin/main:path`.
