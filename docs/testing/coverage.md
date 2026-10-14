# Test coverage

- **Audience:** Contributors, reviewers and maintainers
- **Status:** Living measurement record
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-14

LeaseBook measures what its test suites exercise: which lines and branches of each module ran, and
which API routes the browser tests called. This page records how that is measured, how to run each
measurement locally, how to read the reports, and the baseline the later figures are compared with.

**No figure is gated.** No threshold is configured and no check fails because of a figure.
[ADR-055](../adr/ADR-055-test-coverage-measurement.md) records the tools, the cadence and why.

The Playwright run also carries a check that is not a figure: each scenario in the catalogue has to
produce the journal event types and routes it declares, and the invariants have to hold afterwards.
[End-to-end scenarios](scenarios.md) owns that check and
[ADR-056](../adr/ADR-056-scenario-catalogue.md) records it.

## What the figures mean

A covered line is a line that **ran** during a test. It is not a line a test **checks**. A test that
calls an endpoint and asserts only on the status code covers every line the request touched,
including the ones that computed a figure nobody compared with anything.

So the figures are good at one thing: finding code and routes that no test reaches at all. An
uncovered line is certainly untested. A covered line is only possibly tested. Whether the suites
would notice a covered line being wrong is a different measurement, mutation testing, which is a
later slice of the same work (#547).

Read the figures as a map of gaps. Do not read them as a score.

## The three kinds of run

| Run            | What it measures                                                       | Where it runs                                    |
| -------------- | ---------------------------------------------------------------------- | ------------------------------------------------ |
| .NET suites    | Product code any test project ran, by module                           | Every pull request, in the `Backend` job         |
| Web unit suite | Web source the Vitest suite ran, by area                               | Every pull request, in the `Web` job             |
| Playwright run | Host code, web source and API routes reached by driving a real browser | Pushes to `main`, nightly and on manual dispatch |

The Playwright figures are kept apart from the suite figures on purpose. The .NET suites call
handlers and endpoints directly, so they reach nearly everything. The Playwright figures say what a
person using the screens reaches, which is the smaller and more telling number.

Each run writes a table to the job summary and uploads the full report as an artifact:
`backend-coverage`, `web-unit-coverage`, and `e2e-coverage` from the `Coverage (e2e)` workflow.

## Method

### .NET suites

Every test project references `Microsoft.Testing.Extensions.CodeCoverage`, which adds the
`coverage` options to the test executables. With them, each project writes a Cobertura file of the
product code it ran. ReportGenerator merges the nine files into one report grouped by assembly.

[`tests/coverage.settings.xml`](../../tests/coverage.settings.xml) decides what is counted:

- **Counted:** the product assemblies, which are the seven modules, `SharedKernel`, `Migrator` and
  the `Web` host.
- **Not counted:** the test assemblies, the EF migrations under `src/LeaseBook.Web/Migrations`,
  anything under an `obj` directory, `*.g.cs` and `*.generated.cs` files, and code marked
  `GeneratedCodeAttribute`. That covers source-generator output.

The suites are measured on the Release build CI tests. A project with no test for an assembly still
loads it through the host, so all ten appear; `scripts/coverage-summary.mjs` fails if one is missing.

### Web unit suite

`npm run test:coverage` runs the Vitest suite once with `@vitest/coverage-v8`. The `coverage` block
in `web/vite.config.ts` names every `.ts` and `.tsx` file under `web/src`, so a file no test imports
counts as uncovered and does not go missing. Generated API client code, test files and the test
harness under `src/test` are left out. `npm run test` is the same run without collection.

### Playwright run

`npm run e2e:coverage` runs `scripts/e2e-coverage.mjs`, which does seven things:

1. Starts a `dotnet-coverage` collector in server mode.
2. Runs Playwright with `E2E_COVERAGE=1`. `web/playwright.config.ts` then starts the host as a
   client of the collector's session, with the same `dotnet run` command it otherwise uses.
3. Records, in each test, Chromium's own JavaScript coverage for every page the test opened, and the
   method and path of every API request. The fixture that does this is `web/e2e/coverage-fixture.ts`.
4. Shuts the collector down, which writes the host's Cobertura file.
5. Builds three reports: the host by module, the web source by area, and the route inventory.
6. Reads the journal entries the run posted and checks every scenario against what its tests did.
7. Runs `check-invariants --all` on each database a pass used.

Steps 6 and 7 can fail the command. [End-to-end scenarios](scenarios.md) describes them.

Without `E2E_COVERAGE=1` none of this happens. Every spec imports `test` from `web/e2e/test.ts`,
which is Playwright's own `test` object unless that variable is set, so `npm run e2e` and the
pull-request e2e job run the same tests with no fixture added and the same host command.

A new spec must import `test` from `./test` too. The coverage runner refuses to start while a spec
imports it from `@playwright/test`, because that spec would run unrecorded.

The host is measured on the Debug build `dotnet run` starts, and the suites on the Release build, so
the two reports count a different number of lines for the same code: 24,208 against 21,604 at the
baseline. Compare a percentage with earlier figures from the same kind of run.

The two web reports also count lines differently, because Vitest and the browser report use
different converters from V8's coverage format: 3,239 lines against 3,702 for the same 149 files.

### Route inventory

`scripts/route-inventory.mjs` takes the routes from the OpenAPI document that
`scripts/emit-openapi.mjs` writes, the one the typed client is generated from. It matches each
recorded request to a route template the way ASP.NET Core does: a `{parameter}` segment matches any
value, and a literal segment wins over a parameter.

Each route ends in one of three states:

- **Reached by a browser.** A page made the request and the host answered it.
- **Test client only.** Only the test itself called it, through Playwright's request client
  (`request` or `page.request`). No screen was shown to reach it.
- **Not reached.** Nothing called it. A request the test answered itself with `page.route` is not
  counted, because it never reached the host. The report marks those routes.

Three limits are worth knowing when reading the list:

- The contract holds 131 routes. The host maps four more that the contract leaves out:
  the readiness probe `/api/health/ready`, the development-only `/openapi/v1.json`, the Stripe
  callback `/callbacks/payments/stripe` (mapped only in the Stripe sandbox mode), and the fallback
  that serves the SPA. A request that matches no contract route is listed in its own section of the
  report, so one of these shows up if a test ever calls it.
- The eight `/api/payments` routes are mapped only by a host with payments enabled. Only the two
  fixture passes start one: `--payments` in the simulation mode and `--stand-in` in the Stripe
  sandbox mode. The Stripe callback is called by the stand-in and not by a test, so no request to it
  is recorded.
- `GET /api/health` is listed as not reached. Playwright polls it to see that the host is up, and no
  test requests it. No screen requests it either, so the catalogue records it as a permanent
  exemption.

## Run it locally

### .NET suites

Docker must be running, as for any test run. From the repository root:

```bash
dotnet tool restore
dotnet build -c Release
rm -rf TestResults
Logging__Console__LogLevel__Default=Warning dotnet test -c Release --no-build \
  --results-directory TestResults \
  --coverage --coverage-output-format cobertura \
  --coverage-settings "$PWD/tests/coverage.settings.xml"
dotnet reportgenerator "-reports:TestResults/*.cobertura.xml" "-targetdir:TestResults/coverage" \
  "-reporttypes:Html;Cobertura;JsonSummary"
node scripts/coverage-summary.mjs dotnet --summary TestResults/coverage/Summary.json \
  --title ".NET suite coverage by module"
```

In PowerShell, set the variable first with `$env:Logging__Console__LogLevel__Default = 'Warning'`
and write the settings path as `"$PWD/tests/coverage.settings.xml"`.

- Delete `TestResults` first. Each run adds files with new names, and the report merges every
  Cobertura file it finds there.
- Keep the logging variable. See [Console logging and the test run](#console-logging-and-the-test-run).
- Open `TestResults/coverage/index.html` for the report. `TestResults/coverage/Cobertura.xml` is the
  merged file.

To measure one project, add `--project tests/LeaseBook.Tests.Accounting`. The report then holds only
the assemblies that project loads, and the summary script, which expects all ten, will refuse it.
Read the HTML report instead.

### Web unit suite

From `web/`:

```bash
npm run test:coverage
node ../scripts/coverage-summary.mjs web --summary coverage/unit/coverage-summary.json \
  --title "Web unit coverage by area"
```

Open `web/coverage/unit/index.html` for the report.

### Playwright run

The database is the one `npm run e2e` uses, seeded the same way (demo, cutover, portal and scenario;
see the [local-development runbook](../runbooks/local-dev.md)). The suite changes that database, so start
from a fresh seed, and stop any host or dev server already running: the coverage run starts its own
host under the collector and will not reuse one. From `web/`:

```bash
npm run e2e:coverage                              # the main pass
npm run e2e:coverage -- --payments                # and the simulated-payment pass
npm run e2e:coverage -- --stand-in                # and the Stripe stand-in pass
npm run e2e:coverage -- --payments --stand-in     # all three, as CI runs it
```

Each pass is one run of Playwright against one database, under the same collector:

| Pass       | Flag         | Specs                       | Database                            | Manifest                        |
| ---------- | ------------ | --------------------------- | ----------------------------------- | ------------------------------- |
| `main`     | none         | every spec                  | the one `npm run e2e` uses          | none                            |
| `payments` | `--payments` | `simulated-payment.spec.ts` | `leasebook_payment_fixture`         | `payment-fixture.local`         |
| `stand-in` | `--stand-in` | `stripe-stand-in.spec.ts`   | `leasebook_payment_fixture_standin` | `payment-fixture-standin.local` |

A fourth pass, `sandbox`, is a maintainer's run against a real Stripe sandbox
([runbook](../runbooks/payment-simulation.md#run-the-stripe-sandbox-fixture)). The coverage run never
makes it, and its scenario check reports that pass's scenarios as maintainer-only.

The runner names a fixture pass's database by swapping the database in the main connection string,
and finds its manifest at the repository root. It refuses to start when a manifest is missing.
`--payments` needs the payment fixture, created with `./scripts/payment-fixture.ps1 init`, and
`--stand-in` the stand-in fixture, created with `./scripts/payment-fixture.ps1 init -StandIn`; the
[payment simulation runbook](../runbooks/payment-simulation.md) describes both. In the stand-in pass
Playwright starts the stand-in for Stripe before the host, and the host runs in the Stripe sandbox
mode against it. Nothing in the run calls Stripe.

Each fixture pass writes its Playwright report to a directory of its own: `web/playwright-report/`
for the main pass, then `web/playwright-report-payments/` and `web/playwright-report-stand-in/`.

Everything is written under `web/coverage/e2e/`:

| Path                          | Content                                                     |
| ----------------------------- | ----------------------------------------------------------- |
| `summary.md`                  | The three tables, as CI prints them in the job summary      |
| `host/report/index.html`      | Host coverage by module, class and line                     |
| `host/host.cobertura.xml`     | The collector's own file                                    |
| `web/html/index.html`         | Web coverage by source file and line                        |
| `routes/route-inventory.md`   | Routes reached and not reached                              |
| `routes/route-inventory.json` | The same, with request counts per route                     |
| `requests/`                   | The method and path of every API request, one file per test |
| `tests/`                      | Each test's tags, outcome and start and end times           |
| `journal/`                    | The journal entries each pass posted, by event type         |
| `scenarios/`                  | What each scenario produced against what it declares        |

A failing test fails the command after the reports are written. They then describe a failing run.
The same holds for a scenario that did not produce what it declares and for a violated invariant. A
test marked with `test.fail()` that fails is not a failing test: Playwright counts it as the expected
outcome and the pass succeeds. Its scenario is reported as an expected failure
([End-to-end scenarios](scenarios.md#the-measured-check)).

## Read the reports

- **Start with the summary table.** Look for a module or an area well below the others.
- **Then open the HTML report** and sort by uncovered lines. A red block is code no test ran. A file
  at zero was never loaded.
- **Branch coverage** is the share of decision outcomes taken. A line that ran with its `if` only
  ever true is covered by line and half covered by branch. A low branch figure beside a high line
  figure means the happy path is tested and the alternatives are not.
- **In the route inventory, read the not-reached list first.** A route under a screen people use is
  a missing browser test. A route under "test client only" has an API test where a screen test was
  intended.
- **Compare like with like.** Compare a figure with the same run's earlier figure. The suite
  figures and the browser figures answer different questions and are not meant to converge.

## Baseline

Recorded on 2026-10-12 at commit `c2f6ea5`, from local runs on Linux with the .NET 10.0.112 SDK and
Node 26.8.1. The change that added the measurement touches no product code, so these describe the
product at that commit. CI figures may differ by a few lines where the runner takes a different path
through timing-dependent code.

### .NET suites

2,223 tests across nine test projects, Release build.

| Module       | Classes | Lines covered |  Line | Branches covered | Branch |
| :----------- | ------: | ------------: | ----: | ---------------: | -----: |
| Accounting   |     293 |   3546 / 3651 | 97.1% |        802 / 938 |  85.5% |
| Directory    |     162 |   1677 / 1838 | 91.2% |        383 / 478 |  80.1% |
| Banking      |      49 |     432 / 459 | 94.1% |        201 / 227 |  88.5% |
| Reporting    |      23 |     213 / 215 | 99.1% |          41 / 44 |  93.2% |
| Operations   |      53 |     858 / 888 | 96.6% |        193 / 219 |  88.1% |
| Capabilities |      26 |     483 / 505 | 95.6% |        102 / 112 |  91.1% |
| Payments     |     127 |   1324 / 1356 | 97.6% |       943 / 1093 |  86.3% |
| SharedKernel |      26 |     349 / 372 | 93.8% |        142 / 156 |  91.0% |
| Migrator     |      19 |     304 / 319 | 95.3% |        103 / 136 |  75.7% |
| Web host     |     357 | 10436 / 12001 | 87.0% |      4075 / 4988 |  81.7% |
| **Total**    |    1135 | 19622 / 21604 | 90.8% |      6985 / 8391 |  83.2% |

Two consecutive runs gave identical figures.

### Web unit suite

| Area       | Files | Lines covered |  Line | Branches covered | Branch |
| :--------- | ----: | ------------: | ----: | ---------------: | -----: |
| (src root) |     2 |         1 / 5 | 20.0% |            0 / 2 |   0.0% |
| api        |     5 |       76 / 79 | 96.2% |          82 / 87 |  94.3% |
| app        |     8 |       49 / 60 | 81.7% |          41 / 48 |  85.4% |
| components |    13 |     129 / 141 | 91.5% |        145 / 178 |  81.5% |
| design     |    22 |       69 / 87 | 79.3% |        135 / 162 |  83.3% |
| dev        |     1 |        5 / 17 | 29.4% |            0 / 0 |    n/a |
| features   |    89 |   2333 / 2755 | 84.7% |      2284 / 2867 |  79.7% |
| lib        |     9 |       86 / 95 | 90.5% |          50 / 55 |  90.9% |
| **Total**  |   149 |   2748 / 3239 | 84.8% |      2737 / 3399 |  80.5% |

### Playwright run

These figures were measured again on 2026-10-14 at commit `87faaed`, with the coverage run making
the Stripe stand-in pass for the first time (#545). The change that adds the pass touches no product
code. The two changes before it, which admit the stand-in's address and drive the stand-in
scenarios, did, so the host counts 24,232 lines where it counted 24,208. The two suite baselines
above were not measured again. The earlier figures are in this page's history. At the first
baseline: 82 routes reached by a browser, 57.3% of host lines and 76.0% of web lines. After the
first area of #544: 87 routes, 58.3% and 76.4%. After the second: 90 routes, 58.4% and 77.6%. After
the third: 96 routes, 59.4% and 77.8%. After the fourth: 101 routes, 60.0% and 79.3%. After the
fifth and last, account security and the audit export: 107 routes, 60.9% and 79.8%, which the staff
sign-in with an authenticator code left as they were.

The stand-in pass reaches no route the other two passes had not reached, so the route table is the
same as that measurement's. It reaches host code they do not: the Stripe sandbox adapter and the
callback that receives Stripe's events. Payments rises from 74.8% to 80.8% of its lines, and the Web
host from 50.8% to 53.8%.

All three passes: 130 tests passed and 12 were skipped in the main pass, 4 passed in the
simulated-payment pass, and 8 ran in the stand-in pass, 7 passing and 1 failing as expected. The 12
skipped are the two fixture passes' tests, which run only in their own pass. Each of the 71
scenarios produced what it declares, 1 of them as an expected failure, and none belongs to a pass
the run did not make. `check-invariants --all` was clean in the 5 organizations of the main pass and
in the 2 of each fixture pass. The run was local, so the CI-only visual snapshots did not run; they
assert on pixels and call no extra route.

Host coverage as reached by the browser, Debug build:

| Module       | Classes | Lines covered |  Line | Branches covered | Branch |
| :----------- | ------: | ------------: | ----: | ---------------: | -----: |
| Accounting   |     293 |   2883 / 3966 | 72.7% |        585 / 949 |  61.6% |
| Directory    |     162 |   1641 / 1969 | 83.3% |        343 / 482 |  71.2% |
| Banking      |      49 |     414 / 530 | 78.1% |        134 / 227 |  59.0% |
| Reporting    |      23 |     205 / 241 | 85.1% |          20 / 44 |  45.5% |
| Operations   |      53 |    699 / 1003 | 69.7% |        125 / 217 |  57.6% |
| Capabilities |      26 |     226 / 585 | 38.6% |         41 / 118 |  34.7% |
| Payments     |     127 |   1215 / 1503 | 80.8% |       622 / 1106 |  56.2% |
| SharedKernel |      26 |     332 / 450 | 73.8% |        112 / 156 |  71.8% |
| Migrator     |      19 |     302 / 350 | 86.3% |         79 / 136 |  58.1% |
| Web host     |     357 |  7336 / 13635 | 53.8% |      2363 / 4967 |  47.6% |
| **Total**    |    1135 | 15253 / 24232 | 62.9% |      4424 / 8402 |  52.7% |

Web coverage as reached by the browser:

| Area       | Files | Lines covered |  Line | Branches covered | Branch |
| :--------- | ----: | ------------: | ----: | ---------------: | -----: |
| (src root) |     2 |       16 / 17 | 94.1% |            1 / 2 |  50.0% |
| api        |     5 |       75 / 97 | 77.3% |          57 / 87 |  65.5% |
| app        |     8 |     106 / 113 | 93.8% |          36 / 47 |  76.6% |
| components |    13 |     172 / 196 | 87.8% |        129 / 176 |  73.3% |
| design     |    22 |     132 / 151 | 87.4% |        133 / 162 |  82.1% |
| dev        |     1 |       10 / 14 | 71.4% |            0 / 0 |    n/a |
| features   |    89 |   2357 / 3003 | 78.5% |      1811 / 2842 |  63.7% |
| lib        |     9 |      91 / 111 | 82.0% |          46 / 55 |  83.6% |
| **Total**  |   149 |   2959 / 3702 | 79.9% |      2213 / 3371 |  65.6% |

Route inventory, from 2,458 recorded requests:

| Group             | Routes | Reached by a browser | Test client only | Not reached |
| :---------------- | -----: | -------------------: | ---------------: | ----------: |
| accounting        |     24 |                   18 |                0 |           6 |
| audit             |      4 |                    4 |                0 |           0 |
| auth              |      9 |                    9 |                0 |           0 |
| banking           |      5 |                    4 |                0 |           1 |
| callbacks         |      2 |                    0 |                2 |           0 |
| dashboard         |      1 |                    1 |                0 |           0 |
| directory         |     18 |                   10 |                0 |           8 |
| health            |      1 |                    0 |                0 |           1 |
| onboarding        |      6 |                    6 |                0 |           0 |
| operations        |      5 |                    4 |                0 |           1 |
| payments          |      8 |                    6 |                0 |           2 |
| portal            |      8 |                    7 |                0 |           1 |
| portal-access     |      7 |                    6 |                0 |           1 |
| portal-enrollment |      2 |                    2 |                0 |           0 |
| refund-checks     |     10 |                   10 |                0 |           0 |
| reports           |      4 |                    4 |                0 |           0 |
| search            |      1 |                    1 |                0 |           0 |
| settings          |      9 |                    8 |                0 |           1 |
| statements        |      6 |                    6 |                0 |           0 |
| telemetry         |      1 |                    1 |                0 |           0 |
| **Total**         |    131 |                  107 |                2 |          22 |

The 22 routes no test reached, by what they have in common:

- **Directory records with no screen (8).** `PUT` for owners, properties, units and tenants, `GET`
  and `POST /api/directory/units`, `POST /api/directory/leases` and
  `POST /api/directory/properties/{id}/ownership-transfers`. The web client has a function for each
  and no screen calls one. The screens create an owner, a property and a tenant, and edit only a
  lease's late-fee terms. A property's units arrive with the property.
- **Bank adjustments (1).** No screen records a bank fee, interest or a transfer between accounts.
- **Accounting reads (4).** Owner balances, the owner ledger, deposits and the trust equation. No
  screen requests these routes; the screens that show the same figures get them from other routes.
  The catalogue records these four as permanent exemptions.
- **Reconciliation (1).** Unlocking a reconciliation. No screen unlocks one.
- **Payments review (2).** Retrying a payment, and the unmatched list. Each has a control on the
  Operations page that a browser test cannot bring up. **Retry operation** is offered for a payment
  whose delivery or posting failed for a technical reason six times, with waits of 1, 5, 30, 120 and
  600 seconds between them. The simulator accepts every payment, so nothing a screen or a signed
  notice does makes one fail that way. **Show unmatched notifications** appears once a notice that
  names no payment has been held for ten minutes by the host's clock. A signed notice can produce
  one, but a test would then have to wait the ten minutes out. The catalogue records both as
  permanent exemptions.
- **Settings (1).** Reading one bank account by its id. The web client has a function for it and no
  screen calls it: the screens read the whole list of accounts. The catalogue records it as a
  permanent exemption.
- **Portal invitations (1).** Retrying an invitation. **Retry test delivery** is offered only for an
  invitation whose delivery to the local test inbox failed, and no screen makes a delivery fail. The
  catalogue records it as a permanent exemption.
- **One each:** saving a bank CSV mapping, one tenant-portal payment, one operations run, and
  `GET /api/health`. No screen saves a CSV mapping: the import wizard offers the saved ones and has
  no control that saves one (#567). The web client has a function that reads one tenant-portal
  payment and no screen calls it: the portal reads the tenant's whole list. The catalogue records it
  as a permanent exemption. The web client has a function that reads one operations run with its
  items and no screen calls it: run history lists each run's counts and total and opens nothing.
  Its exemption points at #573, and no exemption points at #544 any more. `GET /api/health` is for
  probes, and the catalogue records it as a permanent exemption.

All nine auth routes are now reached. The account-security tests change no seeded account. Setting
up an authenticator cannot be undone from any screen, and a changed password would follow a seeded
user into every later spec and the next run on the same database. So each test has an administrator
invite a portal tenant at an address made for the run, accepts the invitation, and changes that new
account. The authentication codes are computed in the test from the setup key the screen shows, as
an authenticator app computes them, and the recovery codes are the ones the screen lists. The older
test of required enrollment still answers the session and the enrollment routes itself: a
Development host does not require an administrator to enroll, so no real sign-in is held on that
screen.

One test does sign in as a seeded user: the scenario organization's second administrator, the only
seeded account with an authenticator. Its key is a development-only seed credential committed in
`ScenarioSeeder`, and the test computes the codes from it. No other spec signs in as this user. The
test sets nothing up, changes no password, uses no recovery code and does not sign out. Its one
refused code is followed by an accepted one, which puts the account's count of failed attempts back
at zero, so the account is the same after the test as before it.

The two simulation callbacks are the "test client only" routes. That is by design: a processor calls
them, and no screen does.

## Cost

Measured locally on 2026-10-12. CI runners are slower, so read the differences as proportions.

| Job                    | Without collection | With collection | Added     |
| ---------------------- | -----------------: | --------------: | --------- |
| `Backend`: test step   |               7:17 |            7:57 | 40 s (9%) |
| `Backend`: report step |                    |                 | 3 s       |
| `Web`: unit test step  |             10.6 s |          12.4 s | 1.8 s     |

Both `Backend` figures were taken with the console logger filtered as CI now runs it. The integration
suite is nearly all of the test step, so its slowdown is the same 9%. Each job also uploads one more
artifact.

The Playwright run is outside pull requests, so it adds nothing to them. Its main pass took 5.6
minutes with collection and 4.8 minutes without, on two freshly seeded databases. The whole command,
with both passes and the three reports, took 6:19.

The scenario check and the two invariant runs were added afterwards
([End-to-end scenarios](scenarios.md)). With them the whole command took 6:27 on the same machine,
with the main pass again at 5.6 minutes.

The four ledger-posting tests of #544 add about 16 seconds to the main pass. With them, on
2026-10-12, the plain `npm run e2e` took 5.1 minutes, the main pass with collection took 5.9
minutes, and the whole command with both passes took 6:45.

The three directory-record tests of #544 add about 18 seconds. With them, on 2026-10-12, the plain
`npm run e2e` took 5.2 minutes, the main pass with collection took 6.0 minutes, and the whole command
with both passes took 6:50.

The three banking tests of #544 take about 14 seconds when run by themselves. With them, on
2026-10-12, the main pass with collection took 6.3 minutes. The plain run and the whole command were
not timed again.

The two portal-invitation tests of #544 take about 28 seconds when run by themselves, in the main
pass. The two payment-review tests take about 25 seconds, in the simulated-payment pass, which now
takes 52 seconds. With them, on 2026-10-13, the main pass with collection took 6.7 minutes and the
whole command with both passes took 7:54. Other checks were running on the same machine for the
first minutes of that run, so read those two figures as upper bounds.

The two account-security tests of #544 take about 20 seconds when run by themselves, most of it
inviting and enrolling the new portal account each one uses. The audit-export test takes about 3
seconds. With them, on 2026-10-13, the main pass with collection took 7.2 minutes and the whole
command with both passes took 8:16.

The staff sign-in test of #544 takes about 3 seconds. With it, on 2026-10-13, the main pass with
collection took 7.3 minutes and the whole command with both passes took 8:25.

The Stripe stand-in pass of #545 took 2.6 minutes with collection, the same as its spec takes
without. On 2026-10-14, the whole command with all three passes took 11:20: the main pass 7.4
minutes, the simulated-payment pass 54 seconds, the stand-in pass 2.6 minutes, and the reports, the
scenario check and the three invariant runs the rest. The main and simulated-payment passes ran on
databases seeded for the run, and the stand-in pass on a stand-in fixture made new for it.

## Console logging and the test run

Collection exposed a limit the test run was already close to.

The integration suite's test host logs every SQL command to the console. Over the whole suite that
is about 1.08 GB of output. `dotnet test` holds each test process's output in a single string, and
.NET cannot build a string longer than about 1.07 billion characters. Past that, the CLI throws
`OutOfMemoryException` and reports the run as failed, with every test passing.

Without collection the run stayed just under the limit. With it, the suite runs about 9% longer,
polling code logs more queries, and the first measured run crossed the limit: 2,223 tests passed and
the command exited 1.

The `Backend` job therefore sets `Logging__Console__LogLevel__Default=Warning` for the test step.
That filters the console provider only. The log level itself is unchanged, so a test that captures
log events through its own provider still receives them. With the filter, the same run passes and
the largest process in the run peaks at about 2.7 GB of memory where it had peaked at 4.2 GB.

Use the same variable when collecting locally. A plain `dotnet test` without collection still passes
without it, but it is that close to the limit.
