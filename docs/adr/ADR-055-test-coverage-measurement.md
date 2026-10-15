# ADR-055: Measure what the test suites exercise, and gate nothing on the figures

- **Status:** Accepted
- **Date:** 2026-10-12
- **Deciders:** Jerry Holland
- **Amended by:** [ADR-056](ADR-056-scenario-catalogue.md)

[ADR-056](ADR-056-scenario-catalogue.md) adds two failures to the coverage run that are not figures: a
scenario that did not produce what the catalogue declares, and a violated invariant. It also seeds
the scenario organization in the pull-request e2e job. Nothing below about figures changes.

The [2026-10-14 addendum](#addendum--mutation-testing-with-strykernet-2026-10-14) adds nightly
mutation testing on Accounting and Payments. Its score gates nothing either; what can fail its run,
once a module's gate is on, is a surviving mutant nobody has explained.

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

## Addendum — mutation testing with Stryker.NET (2026-10-14)

Coverage says which lines ran, not whether a test would notice them being wrong. Issue #547 asks that
question of the two modules where a wrong answer is a fiduciary error, Accounting and Payments.

**The tool is Stryker.NET 5.0.0**, pinned as the `dotnet-stryker` local tool in
`.config/dotnet-tools.json`. It runs with its Microsoft Testing Platform runner
(`--test-runner mtp`), which matches the platform these test projects run on
([ADR-032](ADR-032-microsoft-testing-platform-v2.md)). Stryker marks that runner as a preview and
says so on every run. So a survivor is confirmed by hand before anyone acts on it: make the edit, run
the test project, and see it pass, before writing a test for it or recording it as equivalent.

Two limits of that runner shape the setup:

- `test-case-filter` is unavailable with the xUnit v3 MTP runner. A module cannot be mutated against
  part of a test project.
- MTP coverage analysis filters only the mutants no test covers. It does not map tests to mutants, so
  every covered mutant reruns the whole test project. Per-test coverage capture would then cost one
  test session per test and buy nothing, so the configuration asks for one coverage pass
  (`coverage-analysis: all`).

So each module is mutated against a fast test project of its own, never against the integration
suite, which boots the host: Accounting against `LeaseBook.Tests.Accounting`, and Payments against
`LeaseBook.Tests.Payments`, which was added for this. Its tests drive the payment engines directly
over the real schema, with the ledger, eligibility and fee-rule ports replaced by stand-ins the test
controls.

**Report, then fail.** While a module has unexplained survivors, its nightly run only reports. Once a
module is clean, an unexplained survivor fails its run. A survivor is explained when it matches an
entry in the committed equivalents list, `tests/mutation/equivalent-mutants.json`, and every entry
carries its reason. The same file holds each module's gate, and both gates ship off. Stryker's own
`break` threshold is 0 in both configurations, so no score fails a run; the gate is the only failure
apart from Stryker failing.

**It runs nightly and never on a pull request**, in `mutation.yml`, as parallel jobs so that no run
holds up another. Payments is one job. Accounting is two, each mutating part of the module: its first
whole run on a hosted runner took 296 minutes, against GitHub's 360-minute limit for a job, and a job
that times out reports nothing. The method, the measured run times, what counts as an equivalent
mutant and how to run one module locally are in [`docs/testing/mutation.md`](../testing/mutation.md).

Consequences:

- One more pinned development tool, and one more test project in the `Backend` job of every pull
  request.
- When a mutant fails to compile in a way Stryker cannot pin to that mutant, Stryker's safe mode
  marks every mutant in the method as a compile error, and the method goes unmeasured. Today that
  is `PaymentEngine.PostReturnAsync`, where a pattern variable is assigned in one operand of `||`,
  and two Accounting methods, which `docs/testing/mutation.md` names. The summary counts their
  mutants, and a later step can rewrite the code or accept the gap.
- The runner reuses a test process for more than one mutant, and a static field's initializer runs
  once per process, so a mutant inside one either never takes effect or stays in effect for every
  later mutant in that process. Both were confirmed by hand on 2026-10-15. Each static field whose
  initializer Stryker mutates therefore carries a `// Stryker disable once all` comment with its
  reason, the only edit this addendum makes to product code: 51 mutants in Accounting and 17 in
  Payments are reported as ignored, and the tests of those values go unmeasured.
- Stryker measures its per-mutant timeout against one test process running alone, then runs four
  at once, each with its own Postgres container. On a four-core hosted runner that timed out 146
  and then 120 Accounting mutants that a whole run had not, so both configurations set
  `timeout-ratio` to 4. The next run timed out none, and each of those mutants took the status the
  whole run gave it. A mutant that really loops costs about four test runs before it stops.
- The score is per module and is never compared with a threshold. What is acted on is the list of
  survivors, one area at a time.

Reopen this addendum when Stryker's MTP runner leaves preview or learns to select tests per mutant,
when one job's nightly run comes near GitHub's six-hour job limit even after a split, or when both
modules are clean and the measurement widens to the organization-isolation code, as #547 plans.
