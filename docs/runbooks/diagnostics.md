# Runbook: Diagnosing an error from its correlation reference

- **Audience:** Operators and maintainers
- **Status:** Living runbook; canonical error-diagnosis reference
- **Owner:** Maintainers
- **Last reviewed:** 2026-08-03

How to turn the reference an operator sees on screen into the full server-side detail in
Application Insights. See [ADR-025](../adr/ADR-025-error-contract-and-observability.md) for the
contract this runbook operates: an error response never carries internal exception detail, but it
always carries a correlation id an engineer can search on.

## When to use

A user or operator reports an error and the on-screen alert includes a **Reference**. Use this
runbook to go from that string to the request's full server-side trace, logs, and (if one was
logged) the underlying exception.

## Step 1 — find the reference on screen

Every mutation-error alert in the product renders the mapped error message plus, when the server
supplied one, a small monospace line:

```
Reference: 4bf92f3577b34da6a3ce929d0e0e4736
```

It is selectable as a whole (click once, copy). Ask the reporting user for this string, or read it
directly off your own screenshot/session. The reference is a 32-character hex string — the W3C
trace id of the request that produced the error.

One specific case is worth recognizing on sight: if the message reads **"Something went wrong on
our end. Nothing was saved."**, the server's terminal exception handler caught something
unplanned (an `internal_error`, not a typed rejection). The reference is the only way to find out
what happened — nothing about the cause is in the response.

## Step 2 — turn it into an Application Insights query

The reference **is** the trace id, and Application Insights indexes that same value as
`operation_Id`. There is no lookup table and no mapping step — paste it directly into a query
spanning the request, trace, and exception tables:

```kusto
union traces, exceptions, requests
| where operation_Id == "<correlationId>"
| order by timestamp asc
```

This returns, in order, everything logged for that one request:

- The **request** row — route, method, status code, duration.
- Any **trace** rows emitted while handling it — the structured log lines described below, each
  carrying its stable `LogEvents` id and any structured properties (never the raw request body or
  PII — see ADR-025).
- The **exception** row, if one was logged — full exception type, message, and stack trace. This is
  the detail the HTTP response deliberately never carried.

## The `LogEvents` ids

`src/LeaseBook.Web/Observability/LogEvents.cs` defines a stable, numbered `EventId` for every
structured log this contract produces. Track B's B4 alert rules key on these ids, so a query can
filter on `customDimensions.EventId` (or the trace message) instead of matching text:

| Id   | Name                        | Level       | Meaning                                                                                                                                                         |
| ---- | --------------------------- | ----------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1000 | `UnhandledException`        | Error       | The terminal handler caught an exception no typed handler claimed. Always has the exception.                                                                    |
| 1001 | `DomainRejection`           | Warning     | A typed accounting domain rule declined the request (a 404/409/422) — expected, not a defect.                                                                   |
| 1002 | `ValidationRejection`       | Warning     | A command/query or auth DTO failed FluentValidation — a 400.                                                                                                    |
| 1003 | `ImportRowFailed`           | Error       | One row of a migration import failed after parsing; the batch continued. Has the exception.                                                                     |
| 1100 | `SupersedeReversalRace`     | Information | The corrected re-import (supersede) path found the entry already reversed by a racing request; it converges on success anyway — expected, not a defect.         |
| 1101 | `HeldFeesShapeRejected`     | Warning     | A balance-import row's pm_income opening violated the held-fees shape at post time — never a 500. What follows depends on the caller; see below.                |
| 1200 | `InvariantViolation`        | Error       | The nightly sweep found a trust-accounting invariant violated for one org. Fiduciary incorrectness — never routine noise; see below.                            |
| 1201 | `InvariantSweepCompleted`   | Information | The nightly sweep finished with no violations. Its **absence** is itself a signal: a silent night means the job did not run.                                    |
| 1300 | `CapabilityVersionConflict` | Warning     | A bulk-run confirm was rejected (409) because the capability set moved after its preview. Expected and recoverable; a sustained rate is the signal — see below. |

1000-1099 is reserved for host/error plumbing; 1100-1199 is the import-supersede/held-fees domain
(WP-7 — the first block claimed under ADR-025's 1100+ convention); 1200-1299 is scheduled jobs
(WP-11); 1300-1399 is platform capabilities. Later domain areas take the next hundred-block (1400+,
1500+, …) as they add their own structured events.

A `HeldFeesShapeRejected` (1101) means different things on the two import routes, which matters when
you are reading it after an operator report. On a plain balance import the row is recorded as an
error and the batch continues, so expect a 200 with a row error. On a corrected re-import
(`…/supersede`) the whole batch is rolled back and the caller gets a 409: that path has already
posted a reversal by the time the replacement is rejected, and shipping the reversal alone would
remove a live position while reporting the row as failed. So a 1101 on the supersede route means
nothing was written — and it points at a chart-of-accounts divergence (a trust- or deposit-purpose
bank whose `accounts` row is missing or is not `trust_bank`-class), not at the operator's CSV.

## Diagnosing a nightly-sweep event (1200 / 1201)

The nightly trust-invariant sweep runs as a background job, not a request, so there is no on-screen
reference and no `operation_Id` to paste. Start from the event id instead:

```kusto
traces
| where customDimensions.EventId in (1200, 1201)
| order by timestamp desc
```

A **1200** names the affected org and the failed invariant in its message; it is a data condition,
not a transient fault, so the job does not retry — the next night's run is the retry, and the
condition persists until someone posts a correction. Reproduce it on demand with
`dotnet run --project src/LeaseBook.Web -- check-invariants --org <guid>`, which runs the identical
checks (see the local-dev runbook). The sweep also emits a `jobs.invariant_sweep` span with each
violation attached as a span event, so the run's own trace is the correlation handle a request id
would otherwise be.

A run with violations is additionally recorded as **Failed** in Hangfire's job storage, which is
readable without the log pipeline. The Hangfire dashboard is deliberately not mounted
([ADR-001](../adr/ADR-001-background-job-scheduler.md)), so that read is a `leasebook_ops` query
against the `hangfire` schema.

## Diagnosing a capability-version conflict (1300)

A bulk run's confirm echoes back the capability-version token its preview handed out, and the server
compares it against the set it resolves at confirm entry. A mismatch is answered with a 409
`capabilities_changed`; the operator reloads the preview and confirms again, and nothing is posted.

One of these is not an incident — it is the guard working. What is worth acting on is a **rate**:

```kusto
traces
| where customDimensions.EventId == 1300
| summarize count() by bin(timestamp, 5m)
```

Two causes look identical in the log and are separated by asking when the last deploy was.

- **Something is flipping capabilities under live operators.** Expect a burst that starts at a known
  flag or entitlement change and stops after it. The conflicts are correct; the change is what wants
  scheduling differently.
- **Replicas disagree.** The token covers the shape of the source-code capability registry as well as
  the resolved values, so during a rolling deploy that changed the registry, two replicas produce
  different tokens for identical database state. Every preview/confirm pair that spans the two builds
  is rejected. That is the safe direction — the alternative is a confirm posting under a set the
  preview never saw — and it clears on its own once the rollout finishes. A burst that begins at a
  deploy and ends when it completes needs no action.

A steady trickle matching neither pattern is the one to investigate: it points at a capability whose
resolution is not stable for a fixed database state.

## Diagnosing a cross-run period conflict (1301)

Distinct from 1300, and read differently. A bulk run records the money-path capability state it ran
under in `bulk_runs.summary_json`, and a later run for the same org, run type and period is rejected
with a 409 `capabilities_changed_since_prior_run` when its own state disagrees. This exists because a
period is routinely built by more than one run: `source_ref` uniqueness makes a re-run the designed
recovery path, so run 1 can post part of a period under one capability state and run 2 the rest under
another.

```kusto
traces
| where customDimensions.EventId == 1301
```

**Every one of these is worth reading**, unlike 1300, where only a sustained rate is. Each names a
period that two different money-path capability states were about to touch. And re-previewing does
not clear it: a fresh preview cannot change what an already committed run recorded.

Only two things clear it, and both are decisions rather than retries:

- **Restore the earlier state — available only when the same capabilities exist in both releases.**
  Put the money-path capability back the way it was for the run that already posted, and the next
  confirm agrees with it. This is unavailable whenever the _set_ of money-path capabilities changed
  between the two runs; the error message says which case you are in.
- **Acknowledge deliberately.** The confirm accepts `acknowledgeCapabilityChange: true`, which is
  recorded in the new run's `summary_json` as `capabilityChangeAcknowledged`, together with the state
  it overrode in `capabilityChangeFrom`. That is the audit trail for a period computed two ways on
  purpose; there is no way to override this guard without leaving it.

### Adding or removing a money-path capability is period-breaking

`capabilitiesMoneyPath` lists every money-path capability the **registry** defines, with its resolved
value. The registry is source code, so changing which capabilities exist changes that list for every
org at once — with no `feature_flags` row moving, and with no operator action able to put it back.
Both directions do it:

- **Adding one.** Prior runs recorded `["a=off"]`; the new release records `["a=off","b=off"]`. They
  differ. No flag write removes `b` from the list.
- **Removing one.** Prior runs recorded `["a=off"]`; the new release records `[]`. They differ. No
  flag write resurrects `a`.

So **every `(org, run type, period)` that already has a run will 409 on its next run, fleet-wide,
from the moment such a deployment lands.** Addition is the commoner case and is no gentler than
removal.

The comparison deliberately does not ignore names that exist on only one side: a run that posted while
a gate was live and a run that posted before it existed — or after it was deleted — are two behaviours,
and that difference is real. What changes is the message: when the set of names moved, the rejection
says the earlier state _cannot_ be restored and points at deliberate confirmation. If you are reading
"that earlier state cannot be restored", you are looking at a release that changed which capabilities
exist, not at a flag someone flipped.

Add or remove a money-path capability one of these two ways:

1. **Between periods, with the periods closed.** Land the change when every open period's runs are
   done. Nothing 409s, because the next run for those periods is the first one.
2. **With a planned acknowledgement sweep.** If open periods will be re-run, the operators owning them
   must expect the conflict and confirm with `acknowledgeCapabilityChange: true`. Every such run is
   recorded with the state it overrode, which is the audit trail for the change — brief them before
   the deploy, not after the first rejection.

Neither is optional, and neither is a code change: this is release sequencing.

To see what actually differs, read the two runs rather than the log — the log deliberately carries no
capability state:

```sql
SELECT created_at,
       summary_json -> 'capabilitiesMoneyPath'   AS money_path,
       summary_json -> 'capabilityChangeAcknowledged' AS acknowledged
FROM bulk_runs
WHERE org_id = :org_id AND run_type = :run_type
  AND period_year = :year AND period_month = :month
ORDER BY created_at DESC;
```

A run committed before this field existed records no `capabilitiesMoneyPath`, and the guard skips it
rather than inventing a state for it — so the first run of a period after the upgrade never conflicts.

## Production caution: Npgsql `Include Error Detail`

Keep `Include Error Detail=true` **out of** production and staging Npgsql connection strings.
Configuration for those environments should not set it. This is a standing configuration
requirement, not a per-incident step — verify it once per environment, not per diagnosis.
