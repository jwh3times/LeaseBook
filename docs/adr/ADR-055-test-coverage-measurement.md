# ADR-055: Measure what the test suites exercise, and gate nothing on the figures

- **Status:** Accepted
- **Date:** 2026-10-12
- **Deciders:** Jerry Holland
- **Amended by:** [ADR-056](ADR-056-scenario-catalogue.md)

[ADR-056](ADR-056-scenario-catalogue.md) adds two failures to the coverage run that are not figures: a
scenario that did not produce what the catalogue declares, and a violated invariant. It also seeds
the scenario organization in the pull-request e2e job. Nothing below about figures changes.

## Context

Nothing measured what the test suites exercise. The .NET suites, the web unit suite and the
Playwright suite each passed or failed, and none of them said which code or which API routes it had
run. Work on the Stripe sandbox mode showed the cost: a screen with no browser test and a posting
limit found by reading code were both invisible until somebody happened to look. Issue #541 sets out
to exercise the whole product end to end, and it starts with measurement so that the order of the
rest follows figures.

The stack is PRD-locked, so a new test tool is recorded. Three constraints shaped the choice:

- **Nothing leaves CI.** Coverage data names every source file and every route. No hosted coverage
  service is used.
- **The pull-request e2e job is already the slowest.** It takes about six minutes, and issue #317
  tracks its growth. Wrapping the host in a collector and recording browser coverage would slow it.
- **A figure is evidence, and a gate on it is a policy.** There was no baseline to set a threshold
  from. A threshold chosen before one exists either fails for no reason or passes for no reason.

## Decision

**Measure three kinds of run, with first-party tools where they exist.**

| Run                        | Collector                                                              | Pinned in                     |
| -------------------------- | ---------------------------------------------------------------------- | ----------------------------- |
| .NET test projects         | `Microsoft.Testing.Extensions.CodeCoverage`, the MTP extension         | `Directory.Packages.props`    |
| Host under the browser     | `dotnet-coverage`, the same collector as a tool, in server mode        | `.config/dotnet-tools.json`   |
| Web unit suite             | `@vitest/coverage-v8`                                                  | `web/package.json`            |
| Web source under a browser | Chromium's own V8 coverage, read through Playwright's `page.coverage`  | Ships with `@playwright/test` |
| Report building            | ReportGenerator for .NET, `monocart-coverage-reports` for browser data | The tool manifest and `web/`  |

The MTP extension is Microsoft's collector for the test platform
[ADR-032](ADR-032-microsoft-testing-platform-v2.md) selected, so every test project gains the
`coverage` options with no second runner. Its version is tied to the platform's: each release names
one `Microsoft.Testing.Platform` version, and the pinned one names the version `xunit.v3.mtp-v2`
resolves. The two move together. `dotnet-coverage` is held at the same version.

`monocart-coverage-reports` turns the raw V8 coverage of the Vite dev server's modules into a report
per source file, through the dev server's source maps. Playwright documents `page.coverage` and
leaves that conversion to a library; this one caches each test's data on disk and merges it once at
the end, which a run of this size needs.

**One settings file, `tests/coverage.settings.xml`, serves both .NET collections.** It includes the
product assemblies and excludes the test assemblies, the EF migrations and generated code, so the
suite figures and the browser figures are taken over the same code.

**The Playwright run also records every API request it makes.** `scripts/route-inventory.mjs`
matches them against the route templates in the OpenAPI contract the typed client is generated from
([ADR-012](ADR-012-openapi-client-drift-gate.md)). A request a page made is counted apart from one
the test made with its own request client, and a request the test answered itself is not counted. A
request that matches no route in the contract is listed, which is how a mapped route the contract
omits shows up.

**Recording is inert unless asked for.** Every spec takes `test` from `web/e2e/test.ts`. Unless
`E2E_COVERAGE=1`, that is Playwright's own `test` object with no fixture added, and
`playwright.config.ts` starts the host with the command it always used.

**Cadence follows cost.**

- The .NET suites and the web unit suite collect on every pull request, inside the existing
  `Backend` and `Web` jobs.
- The Playwright run collects only on pushes to `main`, nightly, and on manual dispatch, in its own
  workflow, `coverage.yml`. The pull-request e2e job is unchanged.

**Nothing is gated.** No threshold is configured anywhere. A step fails when a test fails or when a
report cannot be produced: a missing coverage file, a product assembly absent from the report, an
empty request recording. It never fails because of a figure. Each run writes its figures to the job
summary and uploads the full reports as artifacts.
[`docs/testing/coverage.md`](../testing/coverage.md) owns the method and the baseline.

## Consequences

- A gap is found by reading a table. The baseline shows which modules and routes no browser test
  reaches, which is the input the later slices of #541 are ordered by.
- The figures say which lines ran, and nothing about whether a test would notice them being wrong.
  The document says so beside the numbers. Mutation testing is the later slice that answers it
  (#547).
- Collection adds time to the `Backend` and `Web` jobs on every pull request. The measured cost is in
  the coverage document.
- Collection exposed a limit that was already close: the integration suite's test host writes about
  1 GB of SQL logging to the console, `dotnet test` buffers a test process's output in one string,
  and the slower instrumented run crossed .NET's string limit with every test passing. The `Backend`
  job now filters the console logger to warnings for the test run. The log level is unchanged.
- Twenty-one specs changed one import, and a new spec has to follow them. The coverage runner
  refuses to start while a spec imports `test` from `@playwright/test`, and a script test checks the
  repository's own specs, so a spec cannot go unrecorded quietly.
- The browser figures arrive after merge, not before it. A pull request shows the suite figures and
  the web unit figures; the browser figures and the route inventory describe `main`.
- Five dependencies are added and pinned: one NuGet package, two .NET tools and two npm packages. Each is a
  development dependency. None ships in the product image.
- The two web figures come from different converters, so a file's line count can differ slightly
  between the unit report and the browser report. Each is compared with its own history.

## Revisit trigger

Reopen when any of these happens:

- A baseline has held long enough to set a floor from, or mutation testing (#547) gives a better
  signal than line coverage. Either one is the point at which gating is worth deciding.
- Coverage collection adds more than about a quarter to the `Backend` job, or the e2e job is sharded
  (#317), which changes what the Playwright run costs and where it can run.
- `xunit.v3.mtp-v2` moves to a platform version the pinned extension does not name, or Microsoft
  stops publishing the extension for the platform in use.
- Coverage data needs to leave CI, for example to annotate pull requests through a hosted service.
