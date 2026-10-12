# ADR-056: Every journal event type and route has a scenario or a recorded exemption

- **Status:** Accepted
- **Date:** 2026-10-12
- **Deciders:** Jerry Holland
- **Amends:** [ADR-055](ADR-055-test-coverage-measurement.md): its rule that no coverage figure is
  gated stands; the coverage run gains two checks that are not figures

## Context

[ADR-055](ADR-055-test-coverage-measurement.md) measures what the Playwright suite reaches and gates
nothing on the figures. Its first baseline showed 47 of 131 API routes that no browser test calls.
Nothing said which of those gaps were known and accepted, and nothing stopped a new route or a new
posting template from joining them unnoticed. The specs are named by milestone (`m3-ledger`,
`m4-banking`), so there was also no list of what the product does set against what a browser test
covers. Issue #543 asks for that list and for a check on it.

Three facts shaped the decision:

- **The journal has two lists, and one contains the other.** A business event is a record derived
  from `AccountingEvent`. A posting template is a place that builds a `PostEntryRequest`, whose
  second argument becomes `journal_entries.event_type`. Every business event has one template of its
  own name. Three templates have no business event: an opening position, a balance forward and a
  reversal. So the set of event types is the template list, and the business events are a subset of
  it. Only the Accounting module can build a `PostEntryRequest`.
- **A presence check is not a figure.** ADR-055 declined to gate on a percentage because no baseline
  existed to set one from. Whether a route has a named scenario or a recorded reason for having none
  is a yes-or-no fact about one item, and the baseline now exists.
- **The pull-request e2e job is the slowest job.** A check that needs a host, a database or a
  browser run cannot go on every pull request without lengthening it.

## Decision

**A catalogue in the repository declares the scenarios.** `web/e2e/scenario-catalogue.json` lists
each scenario: an id, the workflow it drives, the spec that holds its tests, the organization it
drives when it posts to the journal, the journal event types it should post and the API routes it
should call. The same file lists every exemption, which is an event type or a route with no
scenario, each with a reason and a date. Each Playwright test carries its scenario's id as the tag
`@scenario:<id>`. The table in [`docs/testing/scenarios.md`](../testing/scenarios.md) is generated
from the file.

**Declared on every pull request, measured on `main`.** `scripts/scenario-catalogue.mjs` has both
checks.

- The static check runs on every pull request and needs no host and no database. It fails when:
  - a journal event type in the product source, or a route in the API contract, has no scenario and
    no exemption;
  - an item has both, so an exemption cannot outlive the scenario that closes it;
  - the catalogue names an event type or a route that does not exist;
  - a catalogue id has no tagged test in its spec, a test's tag names no catalogue entry, or a test
    carries no scenario tag;
  - the generated table is stale.
- The measured check runs inside the coverage run (`npm run e2e:coverage`, `coverage.yml`), on
  pushes to `main`, nightly and on manual dispatch. For each scenario it takes the requests its
  tests made and the journal entries posted while they ran, and fails when a declared event type was
  not posted or a declared route was not served. It also fails on a posted event type the static
  check's reading of the source does not hold.

**The lists come from the code.** The event types are read from the product source: the
`AccountingEvent` records, and the event type each `PostEntryRequest` names. The check fails when it
cannot read one and does not guess. The routes are read from the OpenAPI document the typed client
is generated from ([ADR-012](ADR-012-openapi-client-drift-gate.md)), with the route inventory's
matcher. No list of either is kept by hand.

**The baseline is a set of dated exemptions.** Each gap that existed on 2026-10-12 is an exemption
with the reason "baseline gap", that date and issue #544, which closes them. The check passed on its
first day and only ratchets: a scenario that covers an exempt item has to delete the exemption.

**Money scenarios drive the scenario organization, and the run checks invariants afterwards.**
`seed --org scenario` is seeded into the e2e database beside `demo`, `cutover` and `portal`. Existing
specs stay on the organizations they use; new money scenarios use the scenario organization. After
the Playwright passes, the coverage run executes `check-invariants --all` on each database a pass
used and fails on a violation. That checks every organization a scenario drove, at the point where
every scenario has finished with it.

**ADR-055 still holds for figures.** No threshold is configured. The coverage run can now also fail
because a scenario did not produce what it declares or because an invariant is violated. Neither is
a comparison with a number.

## Consequences

- A new posting template, business event or route fails the pull request until it has a scenario or
  a written reason. The reason is reviewed with the change.
- The catalogue is the list of what the browser tests cover, by workflow, with the gaps beside it.
- The static check is split across two existing jobs, because no one job has both inputs. The `Web`
  job has the web dependencies Playwright needs to list the tests, and checks the event types, the
  tags and the generated table. The `API client matches OpenAPI contract` job emits the contract,
  and checks the routes. Both run the same command with different options.
- A declaration can be wrong for up to one merge. The static check proves that a scenario claims an
  item, and only the run on `main` proves the claim. A pull request that declares an event its test
  does not post turns `main`'s coverage run red after it merges.
- Journal entries are attributed to a test by time. That is sound while the suite runs on one
  worker, which it does because the specs share seeded organizations. Sharding the e2e job (#317)
  needs a different attribution.
- The event types are read by scanning source text. A `PostEntryRequest` built with a target-typed
  `new(...)` is not seen. A business event added that way still fails the static check, for want of
  a template; a template with no business event added that way is caught by the measured check.
- Event subtypes (a fee's kind, a payment's method, a refund's source) are not catalogued. A
  scenario declares `FeeCharged`, not which kind of fee.
- Invariants are checked once per database after the run and not after each scenario. A violation
  names the organization and not the scenario that caused it.
- The pull-request e2e job seeds one more organization, and every test gains a tag. It gains no
  check: the invariant run and the measured check are in the coverage run only.
- A route counts as called when the host served a request for it from a test of the scenario,
  through a page or through the test's own request client. The route inventory still reports the two
  apart.

## Revisit trigger

Reopen when any of these happens:

- The e2e job is sharded or runs on more than one worker (#317), which breaks attribution by time.
- The baseline exemptions are closed (#544). At that point decide whether a new exemption needs more
  than a reason, and whether the invariant run belongs on pull requests.
- A posting path appears that the source scan cannot read, or a module other than Accounting gains a
  way to write journal entries.
- Catalogue upkeep costs more than the gaps it finds, for example if most pull requests touch it
  only to restate routes.
