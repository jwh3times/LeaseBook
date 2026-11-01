# End-to-end scenarios

- **Audience:** Contributors, reviewers and maintainers
- **Status:** Living catalogue and method
- **Owner:** Maintainers
- **Last reviewed:** 2026-11-01

A scenario is one workflow a person carries out in the product, driven through a real browser by
one or more Playwright tests. This page records how scenarios are catalogued, what CI checks about
them, how to add one, and the catalogue itself.

Every journal event type the product can post and every route in the API contract has a scenario or
a recorded exemption. A pull request that adds one without either fails.
[ADR-056](../adr/ADR-056-scenario-catalogue.md) records the decision.
[Test coverage](coverage.md) owns the coverage figures and the route inventory this builds on.

## The catalogue

[`web/e2e/scenario-catalogue.json`](../../web/e2e/scenario-catalogue.json) is the one source. It
holds two lists.

**Scenarios.** Each entry has:

| Field             | Meaning                                                                                                             |
| ----------------- | ------------------------------------------------------------------------------------------------------------------- |
| `id`              | Lower-case words joined by hyphens. The tag on the tests is `@scenario:<id>`.                                       |
| `workflow`        | What a person does, in one sentence.                                                                                |
| `area`            | The part of the product, for reading the table.                                                                     |
| `spec`            | The file under `web/e2e/` that holds the scenario's tests.                                                          |
| `org`             | The organization the scenario drives. Required when `events` is not empty.                                          |
| `pass`            | `payments`, `stand-in` or `sandbox` for a scenario of one of those passes. Left out for the rest.                   |
| `events`          | The journal event types the scenario's tests post. May be empty.                                                    |
| `routes`          | The API routes the scenario is there to call, as `METHOD /path/{parameter}`. May be empty.                          |
| `expectedFailure` | For a scenario that shows a known limit: why its test is expected to fail, naming the limit. Left out for the rest. |

A pass is one run of the suite against one database. `payments` is the simulated-payment pass, on
the payment fixture. `stand-in` is the Stripe stand-in pass, on the stand-in fixture, where the host
runs in `StripeSandbox` mode against a local stand-in for Stripe
([runbook](../runbooks/payment-simulation.md#run-against-the-stripe-stand-in)). `sandbox` is a
maintainer's run against a real Stripe sandbox, made by hand with `scripts/stripe-sandbox-run.mjs`
([runbook](../runbooks/payment-simulation.md#run-the-stripe-sandbox-fixture)). Any other name is
refused by the static check.

**Maintainer-only passes.** No automated run makes `sandbox`, so the coverage run never judges its
scenarios: the measured check reports each as maintainer-only, and not as a pass the run left out.
For the same reason such a scenario declares no `events` and no `routes`, and the static check
refuses one that does: a scenario nothing automated runs cannot stand in for coverage. Its tests
still carry their scenario tags, and the static check holds them to the catalogue like any other.

`events` is exact for what it lists: every event type named must be posted by a test of the
scenario. `routes` names the routes the scenario exists to cover. A test also calls routes every
page needs, such as the session check, and those are declared once, by the scenario they belong to
most.

**Expected failures.** A scenario can show a known limit: its test asserts what the product should
do and fails today where it says so. Such a test carries the tag `@expected-failure` beside its
scenario tag, and its scenario carries `expectedFailure`. The test calls Playwright's `test.fail()`
only after its setup has passed, so a failure in the setup is an ordinary failure and only the
intended outcome is expected to fail. It declares in `events` and `routes` what it does today up to
that point. When a fix lands, the test passes, Playwright reports that a test expected to fail
passed, and the run turns red; the fix then removes `test.fail()`, the tag and `expectedFailure`.
One scenario is of this kind: `stripe-stand-in-payment-and-return-in-one-payout`, for the limit in
[ADR-054](../adr/ADR-054-stripe-sandbox-adapter.md#known-limits) that a payout holding both a payment
and that payment's return cannot post.

**Exemptions.** An event type or a route with no scenario, each with a `reason` and a `date`. There
are two kinds:

- The reason `baseline gap` marks a gap that existed when the check was introduced. It carries the
  `issue` that closes it. #544 closed the gaps a browser test could close; each one left has its own
  issue, for a screen that does not exist yet.
- Any other reason is written out in the entry, and the entry has no `issue`. It is permanent:
  nothing is planned to close it. The four accounting reads that no screen requests are of this
  kind.

### Journal event types

The check reads the event types from the product source, so a list here would only go stale. The
shape is worth knowing:

- A **business event** is a record derived from `AccountingEvent`, such as `RentCharged`.
- A **posting template** is a place that builds a `PostEntryRequest`. Its event type becomes
  `journal_entries.event_type`.
- Every business event has one template of its own name. Three templates have no business event:
  `OpeningBalance`, `BalanceForward` and `EntryVoided`, the reversal.

So the event types the catalogue covers are the templates, and the business events are among them.
[Accounting](../accounting.md) describes what each one posts.

## What CI checks

| Check    | Where it runs                                   | What fails it                                                          |
| -------- | ----------------------------------------------- | ---------------------------------------------------------------------- |
| Static   | Every pull request, in two jobs of `ci.yml`     | A missing scenario or exemption, a stale exemption, a wrong tag        |
| Measured | The coverage run on `main`, nightly, on request | A scenario that did not produce what it declares; a violated invariant |

### The static check

`node scripts/scenario-catalogue.mjs check` fails when:

- a journal event type in the source, or a route in the contract, has no scenario and no exemption;
- an item has a scenario and an exemption, so the exemption is stale;
- the catalogue names an event type or a route that does not exist;
- a catalogue id has no tagged test in its `spec`, a tag names no catalogue entry, or a test has no
  scenario tag;
- a test of a scenario with `expectedFailure` is not tagged `@expected-failure`, or a test with that
  tag belongs to a scenario without it. `test.fail()` is called while the test runs, so the static
  check reads the tag instead;
- the generated table at the end of this page does not match the catalogue.

No single job has both inputs, so two jobs run it:

- The `Web` job runs `check` with no options. It reads the event types from the source, asks
  Playwright to list the tests, and compares the generated table. Playwright starts no host and no
  browser to list tests.
- The `API client matches OpenAPI contract` job emits the contract, and runs
  `check --no-tags --openapi <document>` for the routes.

### The measured check

`npm run e2e:coverage` runs it after the Playwright passes. For each scenario it takes:

- the requests the scenario's tests made, from the per-test recording the route inventory uses;
- the journal entries posted between each test's start and end.

It fails when a declared event type was not posted, when a declared route was not served by the
host, when a scenario had no test that ran, and when the run posted an event type the static check
did not find in the source. A request the test answered itself with `page.route` does not count. A
request made through the test's own request client does.

Attribution by time is sound because the suite runs on one worker. The journal is read in a
read-only transaction as the database container's own superuser. A runtime role would have to set
the organization context to read it, and only the product's executor may do that
([ADR-048](../adr/ADR-048-per-persona-row-level-security.md)). The run's database is a development
or CI one.

The run then executes `check-invariants --all` on each database a pass used, and fails on a
violation. Every organization a scenario drove is checked once, after the last scenario has finished
with it.

The result is written to `web/coverage/e2e/scenarios/` and joined to the run's summary. It also
lists, without failing, the event types a scenario posted and does not declare, and counts the
scenarios of a maintainer-only pass apart.

A scenario with `expectedFailure` is judged against what it declares, as any other. When it produced
that, it is reported as an expected failure and not as verified, because its workflow does not yet do
what its test says it should. When its test passed, the check fails: the limit it shows may be
fixed.

The coverage run makes all three passes with `npm run e2e:coverage -- --payments --stand-in`. A
scenario of a pass the command was not asked for is reported as belonging to a pass that did not run,
and is not judged. The `stand-in` pass also runs on every pull request, in the
`E2E (Stripe stand-in)` job of `ci.yml`, with `check-invariants --all` after each scenario and once
more when the host has stopped; there it is not measured.

The expected failure passes through two verdicts. Playwright counts a test marked with `test.fail()`
that fails as the expected outcome, so the pass succeeds and the coverage command does not fail for
it. The measured check reads the outcome the test recorded, `failed`, and reports the scenario as an
expected failure once it produced what it declares. A test that passes fails both: Playwright reports
that a test expected to fail passed, and the check fails the scenario.

## Organizations

Money scenarios drive the **scenario organization**, which `seed --org scenario` creates. It has
every posting template, workflow and report in its history, and its figures are golden-locked. The
e2e database is seeded with `demo`, `cutover`, `portal` and `scenario`.

The specs written before the catalogue stay on the organization they already use, and the `org`
field says which. New money scenarios use the scenario organization. The simulated-payment scenarios
use the payment fixture, which is a separate database with two organizations. The two scenarios
that close a review drive the second one, so they share nothing with the two that came first.

The Stripe stand-in scenarios use the stand-in payment fixture, a third database of the same shape.
`./scripts/payment-fixture.ps1 init -StandIn` creates it. Its two organizations are bound to
synthetic connected accounts. Seven scenarios drive the first, and set its card and ACH fee rules to
what Stripe keeps. The expected failure drives the second, whose ACH rule charges a fixed fee beside
Stripe's, so that the two share no payout and no fee rule.

A scenario that changes how an account signs in does not use a seeded account. The database is not
reset between specs, and no screen removes an authenticator, so the change would reach every later
spec and the next run. The account-security scenarios invite a portal tenant at an address made for
the run and change that new account.

## Add or change a scenario

1. Add the entry to `web/e2e/scenario-catalogue.json`.
2. End each test's title with the tag: `test('records a payment @scenario:record-payment', ...)`.
   Playwright reads a tag from the title, and the reports show it as a label.
3. If the scenario covers an exempt event type or route, delete that exemption.
4. From the repository root, regenerate the table and run the static check:

   ```bash
   node scripts/scenario-catalogue.mjs doc
   node scripts/emit-openapi.mjs
   node scripts/scenario-catalogue.mjs check --openapi src/LeaseBook.Web/obj/openapi/LeaseBook.Web.json
   ```

5. Run the measured check for the claim itself. It needs a freshly seeded database; see
   [Test coverage](coverage.md#playwright-run).

   ```bash
   cd web && npm run e2e:coverage -- --payments --stand-in
   ```

To run one scenario's tests: `npm run e2e -- --grep "@scenario:record-payment"` from `web/`.

A new posting template, business event or route needs the same steps, or an exemption with a written
reason. An exemption is reviewed with the change that adds it.

## Limits

- The static check proves that a scenario claims an item. Only the run on `main` proves the claim,
  so a wrong declaration is found after merge.
- A covered route or event type ran. Nothing here says a test would notice it being wrong. See
  [what the figures mean](coverage.md#what-the-figures-mean).
- Event subtypes are not catalogued: a fee's kind, a payment's method, a refund's source.
- The source scan does not see a `PostEntryRequest` built with a target-typed `new(...)`. A business
  event added that way still fails the static check. A template with no business event would be
  caught by the measured check.
- The catalogue lists what the browser tests cover. The .NET suites cover far more, and are measured
  separately.

## Catalogue

Generated from `web/e2e/scenario-catalogue.json` by `node scripts/scenario-catalogue.mjs doc`. Do
not edit between the markers.

<!-- scenario-catalogue:start -->
<!-- prettier-ignore-start -->

75 scenarios, 1 of them an expected failure that shows a known limit. 7 journal event types and 20 routes are exempt.

### Scenarios

| Scenario | Workflow | Area | Spec | Organization | Journal events | Routes |
| :-- | :-- | :-- | :-- | :-- | :-- | :-- |
| `accessibility-portals` | The tenant and owner portals have no WCAG 2 AA violations, light and dark | Accessibility | `a11y.spec.ts` |  |  |  |
| `accessibility-staff-pages` | Every routed staff page, the record details and the refund-check dialogs have no WCAG 2 AA violations, light and dark | Accessibility | `a11y.spec.ts` |  |  | `GET /api/refund-checks/micr/{bankAccountId}`<br>`GET /api/refund-checks/print-settings/{bankAccountId}` |
| `accessibility-command-palette` | The command palette open on results has no WCAG 2 AA violations | Accessibility | `a11y.spec.ts` |  |  |  |
| `accessibility-onboarding` | The onboarding wizard has no WCAG 2 AA violations | Accessibility | `a11y.spec.ts` |  |  |  |
| `mfa-enrollment-by-keyboard` | Be held on required MFA enrollment and complete it by keyboard, with the session and the enrollment routes answered by the test | Account security | `account-security.spec.ts` |  |  |  |
| `mfa-enrollment-and-sign-in` | Set up an authenticator on a new portal account, then sign in with its code and with recovery codes that each work once | Account security | `account-security.spec.ts` |  |  | `POST /api/auth/mfa/enroll`<br>`POST /api/auth/mfa/enroll/confirm`<br>`POST /api/auth/mfa`<br>`POST /api/auth/mfa/recovery` |
| `password-change` | Change the password of a new portal account, which signs its other browser out and stops the old password working | Account security | `account-security.spec.ts` |  |  | `POST /api/auth/change-password` |
| `staff-mfa-sign-in` | An administrator who has an authenticator signs in with a password and a code by keyboard, after a wrong code is refused, and account security shows it enabled | Account security | `account-security.spec.ts` |  |  | `POST /api/auth/mfa` |
| `reconciliation-report-download` | Download the stored report of a finalized reconciliation, by click and by keyboard, and get the month and the statement balance the history shows | Banking | `banking-accounts.spec.ts` |  |  | `GET /api/accounting/reconciliations`<br>`GET /api/accounting/reconciliations/{id}/report` |
| `bank-account-create-and-edit` | Create a bank account in settings by keyboard, with an over-long name refused, see its empty register on Banking, then rename and deactivate it | Settings | `banking-accounts.spec.ts` |  |  | `GET /api/settings/banks`<br>`POST /api/settings/banks`<br>`PUT /api/settings/banks/{id}`<br>`PUT /api/settings/banks/{id}/active` |
| `check-print-settings-and-micr` | Save a bank account's check print offsets by keyboard and its MICR details, with out-of-range offsets and a bad routing number refused, and print the alignment page | Banking | `banking-accounts.spec.ts` |  |  | `GET /api/refund-checks/print-settings/{bankAccountId}`<br>`PUT /api/refund-checks/print-settings/{bankAccountId}`<br>`POST /api/refund-checks/print-settings/{bankAccountId}/alignment`<br>`GET /api/refund-checks/micr/{bankAccountId}`<br>`PUT /api/refund-checks/micr/{bankAccountId}` |
| `sign-in-to-dashboard-balances` | Sign in and see owner ending balances on the dashboard at 0 clicks | Dashboard | `budgeted-flows.spec.ts` |  |  | `GET /api/auth/csrf`<br>`POST /api/auth/login`<br>`GET /api/auth/me`<br>`GET /api/dashboard`<br>`GET /api/onboarding/status`<br>`GET /api/settings/org`<br>`POST /api/telemetry/budget` |
| `palette-jump-to-tenant` | Jump to a tenant from the command palette in 2 interactions | Directory | `budgeted-flows.spec.ts` |  |  | `GET /api/search` |
| `list-row-opens-detail` | Open a record from its list row in 2 interactions | Directory | `budgeted-flows.spec.ts` |  |  | `GET /api/directory/tenants`<br>`GET /api/directory/tenants/{id}` |
| `settings-accounting-basis` | Change the accounting basis in settings and keep it across a reload | Settings | `budgeted-flows.spec.ts` |  |  | `GET /api/settings/org`<br>`PUT /api/settings/org` |
| `directory-browse-records` | Browse owners, properties and tenants from list to detail and page with the quick-switcher | Directory | `directory-navigation.spec.ts` |  |  | `GET /api/directory/owners`<br>`GET /api/directory/owners/{id}`<br>`GET /api/directory/properties`<br>`GET /api/directory/properties/{id}`<br>`GET /api/directory/tenants`<br>`GET /api/directory/tenants/{id}` |
| `property-ownership-transfer` | Transfer a property to another owner, dated today or earlier with a future date refused, and see the held security deposits move to the buyer | Directory | `property-ownership-transfer.spec.ts` | scenario | `DepositResponsibilityTransferred` | `POST /api/directory/properties/{id}/ownership-transfers` |
| `directory-palette-jump` | Jump to an owner, a property, a tenant and a unit from the command palette | Directory | `directory-navigation.spec.ts` |  |  | `GET /api/search` |
| `directory-create-owner` | Create an owner from the owner list by keyboard, with a fee over 100% refused, and see it on its page and in the list | Directory | `directory-records.spec.ts` |  |  | `POST /api/directory/owners` |
| `directory-create-property` | Create a property for a new owner by keyboard, with a blank address refused, and see it on its page, in the list and under its owner | Directory | `directory-records.spec.ts` |  |  | `POST /api/directory/properties` |
| `directory-create-tenant` | Create a tenant from the tenant list by keyboard, with a malformed email refused, and see it on its page and in the list | Directory | `directory-records.spec.ts` |  |  | `POST /api/directory/tenants` |
| `error-and-empty-states` | An expired session, a failed query, a failed post and an empty list each show their designed state | Error states | `error-states.spec.ts` |  |  |  |
| `keyboard-record-payment` | Record a tenant payment by keyboard alone, from the ledger and from the palette action, in 3 interactions | Accounting | `keyboard-only.spec.ts` | demo | `PaymentReceived` | `POST /api/accounting/tenants/{tenantId}/payments`<br>`GET /api/accounting/tenants/{tenantId}/ledger` |
| `keyboard-navigation-and-focus` | Move through the palette, close a dialog and see the focus ring by keyboard | Accessibility | `keyboard-only.spec.ts` |  |  |  |
| `keyboard-reconcile-select-all` | Drive a reconciliation to a $0.00 difference by keyboard | Banking | `keyboard-only.spec.ts` |  |  | `GET /api/accounting/banks/{bankAccountId}/register`<br>`GET /api/accounting/reconciliations` |
| `bank-account-deactivation-guard` | Deactivating a bank account with uncleared items is refused | Settings | `keyboard-only.spec.ts` |  |  | `GET /api/settings/banks`<br>`PUT /api/settings/banks/{id}/active` |
| `ledger-issue-credit` | Charge a tenant, then issue a credit within what they owe from the ledger composer by keyboard, and see the balance fall by the amount | Accounting | `ledger-postings.spec.ts` | scenario | `FeeCharged`<br>`CreditIssued` | `POST /api/accounting/tenants/{tenantId}/charges`<br>`POST /api/accounting/tenants/{tenantId}/credits` |
| `ledger-collect-and-apply-held-funds` | Collect a security deposit and a prepayment, then apply both against a charge, with an over-application refused | Accounting | `ledger-postings.spec.ts` | scenario | `DepositCollected`<br>`PrepaymentReceived`<br>`FeeCharged`<br>`DepositApplied`<br>`PrepaymentApplied` | `POST /api/accounting/tenants/{tenantId}/deposits`<br>`POST /api/accounting/tenants/{tenantId}/prepayments`<br>`POST /api/accounting/tenants/{tenantId}/charges`<br>`POST /api/accounting/tenants/{tenantId}/deposit-applications`<br>`POST /api/accounting/tenants/{tenantId}/prepayment-applications` |
| `ledger-deposit-into-locked-month` | Collect a deposit dated into a reconciled month and get the lock refusal, with nothing posted | Accounting | `ledger-postings.spec.ts` |  |  | `POST /api/accounting/tenants/{tenantId}/deposits` |
| `ledger-export-csv` | Export a tenant ledger as CSV and get the rows and the balance the screen shows | Accounting | `ledger-postings.spec.ts` |  |  | `GET /api/accounting/tenants/{tenantId}/ledger.csv` |
| `ledger-record-and-void-payment` | Record a tenant payment in 3 interactions, then void it with a linked reversal and an audit trail | Accounting | `m3-ledger.spec.ts` | demo | `PaymentReceived`<br>`EntryVoided` | `POST /api/accounting/tenants/{tenantId}/payments`<br>`POST /api/accounting/entries/{entryId}/void`<br>`GET /api/accounting/entries/{entryId}/audit`<br>`GET /api/accounting/tenants/{tenantId}/ledger` |
| `ledger-payment-into-issued-month` | Record a payment into a month whose owner statement is already issued, and see the notice | Accounting | `m3-ledger.spec.ts` | demo | `PaymentReceived`<br>`EntryVoided` | `POST /api/accounting/tenants/{tenantId}/payments`<br>`GET /api/statements/issued-coverage`<br>`POST /api/statements/{ownerId}/deliver` |
| `ledger-composer-guards` | The ledger composer blocks an over-application of held funds, and renders in the dark theme | Accounting | `m3-ledger.spec.ts` |  |  | `POST /api/accounting/tenants/{tenantId}/deposit-applications` |
| `ledger-void-reason-staff-only` | A void's reason stays off the owner statement and shows only as a staff annotation | Accounting | `m3-ledger.spec.ts` | demo | `PaymentReceived`<br>`EntryVoided` | `POST /api/accounting/entries/{entryId}/void`<br>`GET /api/statements/{ownerId}`<br>`GET /api/statements/{ownerId}/internal-notes` |
| `bank-statement-import-and-match` | Import a bank CSV, auto-match and clear a register line, and skip duplicates on re-import | Banking | `m4-banking.spec.ts` |  |  | `POST /api/banking/imports/headers`<br>`POST /api/banking/banks/{bankAccountId}/imports`<br>`POST /api/banking/imports/{importId}/confirm`<br>`GET /api/banking/imports/{importId}/matches`<br>`GET /api/banking/banks/{bankAccountId}/mappings`<br>`GET /api/accounting/banks/{bankAccountId}/register` |
| `bank-reconcile-finalize-and-lock` | Reconcile a trust account to $0.00, finalize, and see the locked month refuse a posting | Banking | `m4-banking.spec.ts` | demo | `PaymentReceived` | `GET /api/accounting/banks/balances`<br>`GET /api/accounting/reconciliations`<br>`POST /api/accounting/banks/clearances`<br>`POST /api/accounting/reconciliations`<br>`POST /api/accounting/reconciliations/{id}/finalize`<br>`POST /api/accounting/tenants/{tenantId}/payments` |
| `bank-reconciliation-unlock-and-refinalize` | As an administrator, unlock a finalized month with a reason, post into it, and finalize it again | Banking | `m4-banking.spec.ts` | demo | `PaymentReceived`<br>`EntryVoided` | `POST /api/accounting/reconciliations/{id}/unlock` |
| `owner-statement-view-export-deliver` | View an owner statement with its fiduciary checks, export it as PDF and CSV, and deliver it | Reporting | `m5-reports.spec.ts` |  |  | `GET /api/statements/{ownerId}`<br>`GET /api/statements/{ownerId}/internal-notes`<br>`GET /api/statements/{ownerId}/pdf`<br>`GET /api/statements/{ownerId}/csv`<br>`POST /api/statements/{ownerId}/deliver` |
| `report-catalog-preview-and-export` | Open the report catalog, preview a report with filters, and export it as CSV | Reporting | `m5-reports.spec.ts` |  |  | `GET /api/reports`<br>`GET /api/reports/{id}/preview`<br>`GET /api/reports/{id}/csv` |
| `month-cycle-rent-run` | Preview and confirm the monthly rent run | Operations | `m6-bulk-operations.spec.ts` | demo | `RentCharged` | `GET /api/operations/runs/{type}/preview`<br>`GET /api/operations/runs/{type}/preview/issued-coverage`<br>`POST /api/operations/runs/{type}/confirm` |
| `month-cycle-late-fee-run` | Preview the late-fee run, and confirm it when a tenant is eligible | Operations | `m6-bulk-operations.spec.ts` |  |  | `GET /api/operations/runs/{type}/preview` |
| `month-cycle-manual-charge` | Post a maintenance fee to a tenant from the ledger composer | Accounting | `m6-bulk-operations.spec.ts` | demo | `FeeCharged` | `POST /api/accounting/tenants/{tenantId}/charges` |
| `month-cycle-reconcile-and-statement` | Reach reconcile mode in 2 clicks, and read an owner statement that ties after the month's postings | Reporting | `m6-bulk-operations.spec.ts` |  |  | `GET /api/accounting/banks/{bankAccountId}/register`<br>`GET /api/statements/{ownerId}` |
| `month-cycle-disbursement-run` | Preview and confirm the owner disbursement run | Operations | `m6-bulk-operations.spec.ts` | demo | `ManagementFeeAssessed`<br>`OwnerDisbursed` | `GET /api/operations/runs/{type}/preview`<br>`POST /api/operations/runs/{type}/confirm` |
| `month-cycle-run-history` | See the confirmed runs in the run history | Operations | `m6-bulk-operations.spec.ts` |  |  | `GET /api/operations/runs` |
| `operations-navigation` | Reach the disbursement run from the dashboard, and switch between the operations screens | Operations | `m6-operations.spec.ts` |  |  | `GET /api/operations/runs`<br>`GET /api/operations/runs/{type}/preview`<br>`GET /api/payments` |
| `onboarding-import-verify-sign-off` | Import a portfolio and its opening balances, correct a non-tying import, verify and sign off | Migration | `m7-onboarding.spec.ts` | cutover | `OpeningBalance`<br>`EntryVoided` | `GET /api/onboarding/status`<br>`POST /api/onboarding/import/{kind}`<br>`POST /api/onboarding/import-balances/{kind}`<br>`POST /api/onboarding/import-balances/{kind}/supersede`<br>`POST /api/onboarding/verification`<br>`POST /api/onboarding/verification/{id}/signoff` |
| `audit-log-review` | Read the audit trail, open one event, and narrow it with filters | Audit | `m8-audit-review.spec.ts` |  |  | `GET /api/audit/events`<br>`GET /api/audit/events/{id}`<br>`GET /api/audit/filters` |
| `audit-log-export` | Export the filtered audit trail as a CSV, by click and by keyboard, and find each export recorded in the trail | Audit | `m8-audit-review.spec.ts` |  |  | `GET /api/audit/events.csv` |
| `compliance-pack-open-period` | Ask for the trust compliance pack for an open period and get a clear refusal | Reporting | `m8-compliance-pack.spec.ts` |  |  | `GET /api/reports/compliance-pack` |
| `late-fee-policy-settings` | Set the organization's late-fee policy and see the late-fee preview follow it | Settings | `m8-late-fee-settings.spec.ts` |  |  | `PUT /api/settings/org`<br>`GET /api/operations/runs/{type}/preview` |
| `lease-late-fee-override` | Set and clear a lease's late-fee override from the tenant ledger | Directory | `m8-late-fee-settings.spec.ts` |  |  | `PUT /api/directory/leases/{id}` |
| `owner-portal-balance-and-statement` | An owner signs in, reads the balance, downloads a statement PDF and signs out | Owner portal | `owner-portal.spec.ts` |  |  | `GET /api/portal/owner/summary`<br>`GET /api/portal/owner/statements`<br>`GET /api/portal/owner/statements/{artifactId}/pdf`<br>`POST /api/auth/logout` |
| `owner-portal-denials` | A resident and a staff member are refused the owner portal | Owner portal | `owner-portal.spec.ts` |  |  |  |
| `portal-invitation-enrollment-revocation` | Invite a tenant and an owner to their portal, enroll each, and revoke the access | Portal access | `portal-enrollment.spec.ts` |  |  | `GET /api/portal-access/{persona}/{targetId}`<br>`POST /api/portal-access/{persona}/{targetId}`<br>`GET /api/portal-access/test-inbox`<br>`POST /api/portal-enrollment/inspect`<br>`POST /api/portal-enrollment/accept`<br>`POST /api/portal-access/{persona}/{targetId}/users/{userId}/revoke` |
| `portal-invitation-replace-and-cancel` | Replace a pending portal invitation, then cancel the replacement, by keyboard, and see each old link stop working | Portal access | `portal-invitations.spec.ts` |  |  | `POST /api/portal-access/invitations/{id}/replace`<br>`POST /api/portal-access/invitations/{id}/cancel` |
| `portal-access-staff-permission` | An administrator stops staff managing portal invitations and access in settings, staff lose the controls, and the administrator allows it again | Portal access | `portal-invitations.spec.ts` |  |  | `PUT /api/settings/portal-access` |
| `refund-check-issue-print-void` | Issue, print and void a deposit refund check in 4 interactions | Accounting | `refund-checks.spec.ts` | demo | `RefundIssued`<br>`EntryVoided` | `GET /api/refund-checks/options`<br>`GET /api/refund-checks`<br>`POST /api/refund-checks`<br>`POST /api/refund-checks/{id}/pdf`<br>`POST /api/refund-checks/{id}/void` |
| `simulated-payment-fee-payout-return` | A tenant pays with a quoted fee; one payout arrives short and one carries a return an administrator posts | Payments | `simulated-payment.spec.ts` | payment fixture | `PaymentReceived`<br>`ProcessorFeeDifference`<br>`EntryVoided` | `PUT /api/settings/payment-fees`<br>`GET /api/portal/tenant/payments/quote`<br>`POST /api/portal/tenant/payments`<br>`POST /callbacks/payments/simulation/payout`<br>`GET /api/payments/settlements`<br>`POST /api/payments/settlements/{id}/post` |
| `simulated-payment-reload-and-bank-evidence` | A simulated payment survives a reload and posts only after separate bank evidence | Payments | `simulated-payment.spec.ts` | payment fixture | `PaymentReceived`<br>`EntryVoided` | `POST /api/portal/tenant/payments`<br>`POST /callbacks/payments/simulation`<br>`GET /api/payments`<br>`POST /api/payments/{id}/return` |
| `simulated-payment-review-closed` | A receipted simulated payment is disputed, and an administrator closes its review with a note by keyboard, with nothing reversed | Payments | `simulated-payment.spec.ts` | payment fixture | `PaymentReceived` | `POST /api/payments/{id}/close-review` |
| `simulated-payment-payout-review-closed` | A payout that does not add up to the bank amount posts nothing, and an administrator closes it with a note by keyboard | Payments | `simulated-payment.spec.ts` |  |  | `POST /api/payments/settlements/{id}/close` |
| `stripe-stand-in-card-to-posted-payout` | A tenant pays by card in the Stripe sandbox mode; the payout waits for the bank and posts when the bank evidence equals the processor's net | Payments | `stripe-stand-in.spec.ts` | stand-in payment fixture | `PaymentReceived` | `GET /api/portal/tenant/payments/quote`<br>`POST /api/portal/tenant/payments`<br>`GET /api/payments`<br>`GET /api/payments/settlements` |
| `stripe-stand-in-ach-to-posted-payout` | A tenant pays by ACH debit in the Stripe sandbox mode under a fee rule that matches Stripe's; the debit is processing, then succeeds, and its payout posts when the bank evidence equals the processor's net | Payments | `stripe-stand-in.spec.ts` | stand-in payment fixture | `PaymentReceived` | `PUT /api/settings/payment-fees`<br>`GET /api/portal/tenant/payments/quote`<br>`POST /api/portal/tenant/payments`<br>`GET /api/payments/settlements` |
| `stripe-stand-in-card-dispute` | Stripe reports a card dispute; the payment goes to staff review with nothing posted, and an administrator closes the review by keyboard | Payments | `stripe-stand-in.spec.ts` | stand-in payment fixture |  | `GET /api/payments`<br>`POST /api/payments/{id}/close-review` |
| `stripe-stand-in-refund` | Stripe reports a refund in full and a refund of part of two receipted card payments; each goes to staff review, nothing is reversed, and an administrator closes both reviews | Payments | `stripe-stand-in.spec.ts` | stand-in payment fixture | `PaymentReceived` | `GET /api/payments`<br>`POST /api/payments/{id}/close-review` |
| `stripe-stand-in-failed-payout` | Stripe says two payouts failed, one waiting for the bank and one already posted; both go to staff review with nothing reversed, and an administrator closes each, after which the payment of the payout that never posted is with staff and the tenant is told a person is checking it | Payments | `stripe-stand-in.spec.ts` | stand-in payment fixture | `PaymentReceived` | `GET /api/payments/settlements`<br>`POST /api/payments/settlements/{id}/close` |
| `stripe-stand-in-bank-amount-differs` | The bank receives a cent less than Stripe's net; the payout goes to review, the right amount afterwards is refused as conflicting, and an administrator closes the payout by keyboard, which sends its payment to staff review | Payments | `stripe-stand-in.spec.ts` | stand-in payment fixture |  | `GET /api/payments/settlements`<br>`POST /api/payments/settlements/{id}/close` |
| `stripe-stand-in-recorded-payout` | The payout a sandbox made on 2026-10-09 arrives as recorded with its fifteen lines; it goes to review, bank evidence releases nothing and offers no post, and the review is closed | Payments | `stripe-stand-in.spec.ts` | stand-in payment fixture |  | `GET /api/payments/settlements`<br>`POST /api/payments/settlements/{id}/close` |
| `stripe-stand-in-payment-and-return-in-one-payout` | A payout holds an ACH debit and that debit's return; bank evidence equal to the net should let it post, with the receipt and its reversal **Expected to fail:** ADR-054 known limit: a payout that holds both a payment and that payment's return cannot post. Today it goes to review as unsupported_settlement and can only be closed; the test fails where it asserts that the payout posts. | Payments | `stripe-stand-in.spec.ts` | stand-in payment fixture |  | `PUT /api/settings/payment-fees`<br>`GET /api/payments/settlements` |
| `stripe-sandbox-start` | A maintainer's run against a real Stripe sandbox: an administrator sets Stripe's own fee rules, a tenant pays $500.00 by ACH and $500.00 by card, and both read paid and on their way to the bank, with nothing posted | Payments | `stripe-sandbox.spec.ts` | copy of the Stripe sandbox fixture |  |  |
| `stripe-sandbox-resume` | The same run, resumed once Stripe has paid out: an administrator finds the payout of those two payments on Operations, in review with its reason, or waiting for the bank with the processor's $1,000.00 and two payment lines not on the ledger | Payments | `stripe-sandbox.spec.ts` | copy of the Stripe sandbox fixture |  |  |
| `spa-boot` | The application loads and shows the sign-in screen | Platform | `smoke.spec.ts` |  |  |  |
| `tenant-portal-staff-denied` | A staff member is refused the tenant portal and keeps the dashboard | Tenant portal | `tenant-portal.spec.ts` |  |  |  |
| `tenant-portal-ledger-and-security` | A resident signs in, reads the ledger, reaches account security and signs out | Tenant portal | `tenant-portal.spec.ts` |  |  | `GET /api/portal/tenant/ledger`<br>`GET /api/portal/tenant/payments`<br>`POST /api/auth/logout` |

### Exemptions

| Reason | Count |
| :-- | --: |
| No screen requests it; container probes and the test runner poll it to see that the host is up. | 1 |
| No screen requests it; the portal reads the tenant's whole list of payments. | 1 |
| No screen requests it; the screens read the whole list of bank accounts. | 1 |
| No screen requests it; the screens that show these figures get them from other routes. | 4 |
| The control appears only after six failed attempts with waits of up to ten minutes, and the simulated processor accepts every payment. | 1 |
| The control appears only for a notice held for ten minutes, which a test would have to wait out. | 1 |
| The control appears only when delivery to the local test inbox failed, and no screen makes a delivery fail. | 1 |
| baseline gap | 17 |

#### Journal event types with no scenario

| Event type | Reason | Date | Issue |
| :-- | :-- | :-- | :-- |
| `BalanceForward` | baseline gap | 2026-10-12 | #556 |
| `BankFeeCharged` | baseline gap | 2026-10-12 | #553 |
| `InterestEarned` | baseline gap | 2026-10-12 | #553 |
| `OwnerContribution` | baseline gap | 2026-10-12 | #556 |
| `PMFeesSwept` | baseline gap | 2026-10-12 | #556 |
| `TrustTransfer` | baseline gap | 2026-10-12 | #553 |
| `VendorPaid` | baseline gap | 2026-10-12 | #556 |

#### Routes with no scenario

| Route | Reason | Date | Issue |
| :-- | :-- | :-- | :-- |
| `GET /api/accounting/owners/balances` | No screen requests it; the screens that show these figures get them from other routes. | 2026-10-12 |  |
| `GET /api/accounting/owners/{ownerId}/ledger` | No screen requests it; the screens that show these figures get them from other routes. | 2026-10-12 |  |
| `GET /api/accounting/deposits` | No screen requests it; the screens that show these figures get them from other routes. | 2026-10-12 |  |
| `GET /api/accounting/trust-equation` | No screen requests it; the screens that show these figures get them from other routes. | 2026-10-12 |  |
| `POST /api/accounting/banks/{bankAccountId}/adjustments` | baseline gap | 2026-10-12 | #553 |
| `PUT /api/directory/owners/{id}` | baseline gap | 2026-10-12 | #558 |
| `PUT /api/directory/properties/{id}` | baseline gap | 2026-10-12 | #558 |
| `GET /api/directory/units` | baseline gap | 2026-10-12 | #558 |
| `POST /api/directory/units` | baseline gap | 2026-10-12 | #558 |
| `PUT /api/directory/units/{id}` | baseline gap | 2026-10-12 | #558 |
| `PUT /api/directory/tenants/{id}` | baseline gap | 2026-10-12 | #558 |
| `POST /api/directory/leases` | baseline gap | 2026-10-12 | #558 |
| `GET /api/health` | No screen requests it; container probes and the test runner poll it to see that the host is up. | 2026-10-12 |  |
| `GET /api/settings/banks/{id}` | No screen requests it; the screens read the whole list of bank accounts. | 2026-10-12 |  |
| `POST /api/banking/banks/{bankAccountId}/mappings` | baseline gap | 2026-10-12 | #567 |
| `POST /api/portal-access/invitations/{id}/retry` | The control appears only when delivery to the local test inbox failed, and no screen makes a delivery fail. | 2026-10-12 |  |
| `GET /api/portal/tenant/payments/{id}` | No screen requests it; the portal reads the tenant's whole list of payments. | 2026-10-12 |  |
| `POST /api/payments/{id}/retry` | The control appears only after six failed attempts with waits of up to ten minutes, and the simulated processor accepts every payment. | 2026-10-12 |  |
| `GET /api/payments/unmatched` | The control appears only for a notice held for ten minutes, which a test would have to wait out. | 2026-10-12 |  |
| `GET /api/operations/runs/{id}` | baseline gap | 2026-10-12 | #573 |

<!-- prettier-ignore-end -->
<!-- scenario-catalogue:end -->
