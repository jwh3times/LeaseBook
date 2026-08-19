# Runbook: Diagnosing an error from its correlation reference

- **Audience:** Operators and maintainers
- **Status:** Living runbook; canonical error-diagnosis reference
- **Owner:** Maintainers
- **Last reviewed:** 2026-08-12

How to turn the reference an operator sees on screen into the full server-side detail in
Application Insights. See [ADR-025](../adr/ADR-025-error-contract-and-observability.md) for the
contract this runbook operates: an error response never carries internal exception detail, but it
always carries a correlation id an engineer can search on.

## Telemetry collection boundary

LeaseBook deliberately uses the standalone Azure Monitor exporter rather than the ASP.NET Core
Azure Monitor distro. Exporters are attached only when `APPLICATIONINSIGHTS_CONNECTION_STRING` is
non-empty; without it, no telemetry leaves the process. The configured signal surface is ASP.NET Core
request traces, the custom `LeaseBookTelemetry` ActivitySource, and correlated structured logs. It
does not include the distro's automatic HTTP-client/SQL tracing, standard metrics, performance
counters, or Live Metrics.

Query-string values are redacted by the current ASP.NET Core instrumentation, and
`DeliverTelemetryTests` guards the statement-delivery email specifically. Do not set
`OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION=true`: that changes the security
boundary this runbook assumes. The distro uses that unsafe value by default, which is why ADR-025's
2026-08 amendment defers adoption until metrics or Live Metrics are explicitly required and the
privacy override can be validated in a live telemetry environment.

The exporter retries transient ingestion failures and uses offline storage by default. That behavior
does not require the distro. Microsoft Entra-authenticated ingestion is also available on the
standalone exporter but is not wired until the live Azure identity and role assignment are
deployment-validated. See the
[distro evaluation](../research/azure-monitor-opentelemetry-distro.md) for the full comparison.

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
| 1300 | `CapabilityVersionConflict` | Warning     | A run confirmation was rejected (409) because the capability set moved after its preview. Expected and recoverable; a sustained rate is the signal — see below. |

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

A run confirmation echoes back the capability-version token its preview handed out, and the server
compares it against the set it resolves at run-confirmation entry. A mismatch is answered with a 409
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
  different tokens for identical database state. Every preview/confirmation pair that spans the two builds
  is rejected. That is the safe direction — the alternative is a run confirmation posting under a set the
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
  run confirmation agrees with it. This is unavailable whenever the _set_ of money-path capabilities changed
  between the two runs; the error message says which case you are in.
- **Acknowledge deliberately.** The run confirmation accepts `acknowledgeCapabilityChange: true`, which is
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

## Changing a capability in production

Everything above that says "put the money-path capability back the way it was", "restore the earlier
state", or "turn the flag off" needs a way to actually do it. There is exactly one, and it is not a
console: capability state is written only by the `capabilities` CLI verb, and prod's Postgres is
VNet-injected with no public endpoint ([ADR-027](../adr/ADR-027-prod-private-networking-and-migration-job.md)),
so nothing outside the Container Apps environment can reach it. The verb therefore runs as a
manual-trigger Container Apps Job, `lb-<env>-capabilities`, in the same way prod migrations do.

Locally and in any environment whose database you can reach, run the verb directly instead — the job
exists for the network boundary, not for the verb:

```bash
dotnet run --project src/LeaseBook.Web -- capabilities list
```

### The invocation — always `--yaml`

**Do not use the `--args` / `--env-vars` flags for this job.** `--args` is an argparse `nargs='*'`
list, and argparse classifies any unknown token beginning with `-` as an option, so the list stops at
the first one. `--org` is required by `grant`, `revoke`, `cohort add` and `cohort remove`, and is
optional on `list`; `--stale` is optional on `list`. All of them break:

```console
$ az containerapp job start --name lb-prod-capabilities --resource-group lb-prod-rg \
    --args "capabilities" "list" "--org" "demo"
ERROR: unrecognized arguments: --org demo
```

`--org=demo` fails the same way, and quoting the whole thing into one string is worse: it arrives as a
single argv element, the verb does not match it, and the container boots the ASP.NET host instead of
running a command. The `--yaml` form takes an execution template verbatim and is the only form that
carries every subcommand, so it is the only one documented here — one form, uniformly correct, rather
than a short one that silently misbehaves on exactly the arguments you need in an incident.

```bash
RG=lb-prod-rg
JOB=lb-prod-capabilities

# The image the RUNNING app is on — read from the app, never assumed from the job.
IMAGE=$(az containerapp show --name lb-prod-app --resource-group "$RG" \
  --query 'properties.template.containers[0].image' -o tsv)

sed "s|IMAGE_PLACEHOLDER|$IMAGE|" infra/jobs/capabilities-exec.yaml > /tmp/exec.yaml

# Now edit /tmp/exec.yaml: set the `args:` list, and set LEASEBOOK_OPERATOR's `value:` to your name.
# It ships blank on purpose, so an unedited copy is refused rather than recorded against nobody.

# Keep the execution name the start hands back — it is the only reliable way to identify YOUR run.
EXEC=$(az containerapp job start --name "$JOB" --resource-group "$RG" \
  --yaml /tmp/exec.yaml --query 'name' -o tsv)
```

This needs a **repository checkout at the commit the running app was built from** — not just `az`. The
YAML is pinned to that commit's Bicep, and the capability registry is source code, so a checkout of a
different commit can name capabilities the running replicas do not have (and vice versa). `$IMAGE`
above ends in the git SHA the deploy promoted; check that out.

**Do not hand-write that YAML.** It is committed at
[`infra/jobs/capabilities-exec.yaml`](../../infra/jobs/capabilities-exec.yaml) and pinned against the
Bicep by `CapabilitiesJobTemplateTests`, which checks the container name, every environment variable,
the `secretRef` spelling, and that the `args:` list is something the verb actually accepts. Copying it
is not laziness — see below.

Change only the `args:` list to run a different subcommand. Examples:

| Intent                   | `args:`                                                                               |
| ------------------------ | ------------------------------------------------------------------------------------- |
| Kill switch              | `capabilities`, `flag`, `disable`, `<name>`                                           |
| Re-enable                | `capabilities`, `flag`, `enable`, `<name>`                                            |
| Restore cohort/default   | `capabilities`, `flag`, `clear`, `<name>`                                             |
| Entitle one organization | `capabilities`, `grant`, `<name>`, `--org`, `<org-id>`                                |
| Withdraw                 | `capabilities`, `revoke`, `<name>`, `--org`, `<org-id>`                               |
| Cohort, whole org        | `capabilities`, `cohort`, `add`, `<name>`, `--org`, `<org-id>`                        |
| Cohort, one user         | `capabilities`, `cohort`, `add`, `<name>`, `--org`, `<org-id>`, `--user`, `<user-id>` |
| Undo a cohort rule       | `capabilities`, `cohort`, `remove`, `<name>`, `--org`, `<org-id>`                     |
| Read one organization    | `capabilities`, `list`, `--org`, `<org-id>`                                           |

`cohort remove` is the exact inverse of `cohort add`: without `--user` it targets the org-wide rule
only, so the two invocations have to match token for token or the removal silently matches nothing
(the verb refuses in that case rather than reporting "removed 0").

`flag disable` and `flag clear` are not synonyms. Disable writes an explicit false override, which
beats every cohort. Clear deletes that override, so resolution falls through to cohort membership and
then the registry default; it refuses if no override row exists. A user-level cohort add also refuses
unless that user belongs to the `--org` supplied on the same command.

Why every field is present rather than only the ones you are changing: `az containerapp job start`
does **not** merge with the job's template. It sends the execution template as given, so anything
omitted is simply absent from the execution — an omitted `env` leaves the container with no connection
string, an omitted `name` does not match the template's container, and omitted `resources` leaves no
resources block. The template in the deployment is the default for a _bare_ start, not a base to
inherit from.

### Why the file is copied rather than retyped

The execution template is deserialized by a matcher that compares every key **case-sensitively** and
**discards anything it does not recognise, with no error and no warning.** Confirmed against the CLI's
own deserializer:

| Written       | Written wrong                             | What is lost                                          |
| ------------- | ----------------------------------------- | ----------------------------------------------------- |
| `containers:` | `Containers:`                             | **everything** — an empty `{}` envelope is sent       |
| `env:`        | `Env:`                                    | **the whole environment**, container otherwise intact |
| `args:`       | `Args:`                                   | the subcommand                                        |
| `image:`      | `Image:`                                  | the image                                             |
| `resources:`  | `Resources:`                              | the resources block                                   |
| `name:`       | `Name:`                                   | that entry's name                                     |
| `value:`      | `Value:`                                  | that variable's value                                 |
| `secretRef:`  | `secretref:`, `secret_ref:`, `SecretRef:` | that variable's secret reference                      |

Every one of those was confirmed against the CLI's own deserializer, and **not one of them errors**. So
"use the wire names" is not a rule anyone can apply from memory. `secretref` is the _likely_ typo rather
than an exotic one, too: the `--env-vars` flag form spells it `secretref:` in lower case, and
`deploy-prod.yml` uses exactly that spelling for the migrator.

Two of these are worse than the rest.

A dropped `secretRef` or `env` leaves the container with no connection string, so it starts and then
dies — **which is precisely how it dies when `defaultSecretUri` has not been wired yet**, a state
`infra/README.md` describes as normal before the role bootstrap. The plausible diagnosis is therefore
the wrong one, and the operator goes to debug Key Vault RBAC. The verb now names the YAML ahead of Key
Vault when it hits this, but the copied file and its test are what stop you getting there.

A dropped `containers` is worse still and is **not yet fully understood**: the CLI POSTs an empty
`{}` execution template, and what the resource provider does with that is unverified. If it rejects it,
you get an error. If it instead starts the job on its **deployed default template**, the execution runs
`capabilities list` — so a `flag disable` would print a capability table, exit 0, and change nothing.
Until that is settled on a real deployment, treat "the output looks like a listing I did not ask for"
as a suspected mis-cased `containers:`, not as a successful run.

`LEASEBOOK_OPERATOR` names the person or system accountable for the change. It is **required** for
every mutating subcommand outside Development — the verb refuses without it, before touching the
database. That is not ceremony: `platform_audit_events` is append-only in both planes, this container
has no human identity of its own, and a row recorded against nobody can never be corrected. `list`
does not need it and is never refused; an operator must always be able to read state.

A bare `az containerapp job start --name "$JOB" --resource-group "$RG"` with no flags at all runs the
template as deployed, whose default args are `capabilities list`. That is the smoke test — it proves
image pull, Key Vault resolution and database reachability in one read-only execution.

### Reading the result

`az containerapp job start` returns when the execution **starts**, not when it finishes, so its exit
code says nothing about whether the change applied.

Use the `$EXEC` the start handed back. Do **not** reach for
`sort_by([], &properties.startTime)[-1]` — "the most recent execution" is a previous one whenever the
start failed or has not registered yet, and reading a stale `Succeeded` here is how you conclude a
change landed when it did not, and then append a second event re-running it.

```bash
az containerapp job execution show --name "$JOB" --resource-group "$RG" \
  --job-execution-name "$EXEC" --query 'properties.status' -o tsv

az containerapp job logs show --name "$JOB" --resource-group "$RG" \
  --execution "$EXEC" --container capabilities --tail 200
```

If `$EXEC` comes back empty, the start did not take — do not fall back to listing executions and
picking the newest. Fix the start and re-run, or list executions and identify yours by inspection.

Note the parameter names differ between the two: `job execution show` takes `--job-execution-name`,
`job logs show` takes `--execution` and requires `--container`. `--container capabilities` matches
because the YAML above names the container `capabilities`; an execution started without a name would
be named after the job and this would return nothing.

`job logs` comes from the **preview** `containerapp` CLI extension, while `job start` and
`job execution` are core. On a non-interactive shell there is no tty to accept the install prompt, so
set `AZURE_EXTENSION_USE_DYNAMIC_INSTALL=yes_without_prompt` (or pre-install the extension) before
reaching for logs from CI or a script.

**A timed-out or failed execution is not evidence that nothing was written.** The job's
`replicaTimeout` is a wall-clock deadline over image pull, host boot and the transaction, so it can
fire after the transaction committed. `grant` and `revoke` append events rather than setting state, so
re-running one is not idempotent — it writes a second event. Always read before you re-run, by
re-issuing the same YAML with `args:` changed to `capabilities`, `list`, `--org`, `<org-id>`.

### `list --stale` is not a production command

`capabilities list --stale` appends the capability age report. Age is derived from **git history**, and
the container image carries none, so from this job every row reports `UNKNOWN` and the report says so
before printing anything. It parses and runs — it is simply not informative here.

It is not expensive, just useless: `CapabilityAge.ResolveAsync` short-circuits in `FindRepoRoot()`
before spawning any git subprocess, so running it from the job costs nothing rather than 15s per
capability.

The enforcing gate is `CapabilityAgeTests` in CI, which calls the same `CapabilityAge.IsStale`. Run
`--stale` from a checkout when you want the answer:

```bash
dotnet run --project src/LeaseBook.Web -- capabilities list --stale
```

### What a flag flip does and does not reach

A flag write commits and issues `NOTIFY` in the same transaction, so running replicas normally drop
their cached value within a second — a kill switch does not wait for a deploy or a restart. Do not
treat that as the guarantee, though: the 30-second per-replica cache TTL is the correctness floor and
`NOTIFY` is only a latency optimization, so a replica whose listener has dropped still converges, just
by the slower route. **Wait out the TTL before concluding a flip did not take.** It does **not** reach
a bulk run already in flight: the run engine freezes
its capability set at preview and rejects a run confirmation whose set has moved, which is the 1300 above.
Flipping a money-path capability mid-rollout is what produces that burst.

## Production caution: Npgsql `Include Error Detail`

Keep `Include Error Detail=true` **out of** production and staging Npgsql connection strings.
Configuration for those environments should not set it. This is a standing configuration
requirement, not a per-incident step — verify it once per environment, not per diagnosis.
