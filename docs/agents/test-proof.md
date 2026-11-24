# Test proof

- **Audience:** Coding agents and maintainers configuring engineering skills
- **Status:** Living configuration
- **Owner:** Maintainers
- **Last reviewed:** 2026-11-23

How `test-prover` runs one test on its own here, and how it tells a run that tested nothing from a
pass.

## Test commands

- repo root: the .NET suites, `tests/LeaseBook.Tests.*/**/*Tests.cs` (xUnit v3 on Microsoft
  Testing Platform), one test project per folder under `tests/`.
  - One file (a file holds one class):
    `f=<file>; dotnet test --project "$(echo "$f" | cut -d/ -f1-2)" --filter-class "*.$(basename "$f" .cs)"`;
    one test by name: the same with
    `--filter-method "*.$(basename "$f" .cs).<name>"`. A theory counts each of its cases.
  - The count is MTP's `Test run summary:` block, which `mutate.mjs` reads already.
  - The scratch worktree needs nothing: `dotnet test` restores and builds the project on its own.
  - Not provable without a running Docker engine: the projects that use Testcontainers
    (`Accounting`, `Capabilities`, `Integration`, `Operations`, `Payments`, `Tenancy`). Their tests
    fail at fixture start without one.
  - `dotnet test` can end `Zero tests ran` (exit 5) on a project whose tests pass
    (`local-runs.md`). That run is not proof either way; the built host
    `tests/<Project>/bin/Debug/net10.0/<Project>.exe -class <FQN>` runs the class instead.
- `web/`: Vitest, `src/**/*.{test,spec}.{ts,tsx}`.
  - One file: `npx vitest run <file>`; one test by name: `npx vitest run <file> -t "<name>"`.
  - The count is Vitest's `Tests  N passed (N)` line, which `mutate.mjs` reads already.
  - The scratch worktree links `web/node_modules`.
- repo root: `scripts/*.test.mjs`, node's built-in test runner.
  - One file: `node --test <file>`; one test by name:
    `node --test --test-name-pattern="<name>" <file>`, with `<name>` regex-escaped.
  - The count is node's `ℹ tests N` line, which `mutate.mjs` reads already.
  - The scratch worktree needs nothing: the root `package.json` has no dependencies.
- `web/e2e/`: Playwright, `*.spec.ts`. Not provable here: it needs Postgres and a seeded host
  (`npm run e2e:fresh`, `docs/runbooks/local-dev.md`).
