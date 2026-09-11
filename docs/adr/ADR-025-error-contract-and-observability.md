# ADR-025: Error contract, correlation ids, and diagnostic logging

- **Status:** Accepted
- **Date:** 2026-07-19
- **Deciders:** Engineering
- **Amended by:** [ADR-031](ADR-031-compiled-il-architecture-guards.md) — the single-factory guard
  moved from matching source text to reading compiled IL, which retired the two syntactic scan
  limitations this ADR originally named (see the Decision).

## Context

The standing engineering requirement for this product is: nothing should fail silently; errors
should be shown to users in a way that is valuable and exposes no PII or internal technical detail;
technical detail should be logged where an engineer can find it; and the loop between the two should
be closeable from a support conversation. Before this WP, none of that chain held:

- `ILogger` was injected in only three files repository-wide (`TelemetryCommandDecorator`,
  `TelemetryQueryDecorator`, `LocalStatementDelivery`) — nothing in Onboarding, `PostingService`,
  `VerificationService`, or either existing exception handler logged anything.
- `Program.cs` wired OpenTelemetry **tracing** only (`WithTracing` + `AddAzureMonitorTraceExporter`);
  there was no logging exporter, so whatever `ILogger` output did exist went nowhere once shipped.
- Nothing in source, tests, or the web client carried a correlation/trace id. A user reporting "it
  said import failed" gave an engineer nothing to search Application Insights on.
- `EntityImportService.cs` wrote the raw `Exception.Message` from EF Core/Npgsql/FluentValidation
  straight into `import_rows.errors_json`, and the onboarding UI rendered it unfiltered — constraint,
  table, and column names reached the operator.
- Only two `IExceptionHandler`s were registered (validation, typed accounting errors). Any other
  exception fell through `UseExceptionHandler()` to the framework default: an empty 500 with no code,
  no log, nothing to act on.
- 28 call sites across 11 files built `ProblemDetails` responses directly (`Results.Problem`,
  `TypedResults.Problem`, `Results.ValidationProblem`), each choosing its own `title`/`detail`
  independently; several set `title` but never the `code` extension every frontend mapper reads,
  making the intended error copy unreachable. Four more sites returned an ad-hoc
  `Results.NotFound(new { error = "…" })` shape no frontend mapper could read at all. Five frontend
  features (`banking.ts`, `onboarding.ts`, `useRuns.ts`, `ledgerMutations.ts`, `reports.ts`) each
  carried their own copy-pasted error-normalizing mapper, and they had already drifted —
  `reports.ts` silently dropped the validation-error branch the other four had.

This was discovered while gating WP-7 (import correction/supersede), not invented for it — the gate
found the observability half of "done" unachievable on the current surface, which is why it is its
own WP (WP-14) rather than a WP-7 subtask. It also has to land before Track B's B4 (telemetry release
gate + alerting), which keys its alert rules on the correlation-id contract and the `LogEvents` ids
this ADR defines. Azure is not deployed yet (B1 pending), which makes this the cheapest possible time
to make the change: the log exporter added here is conditional on
`APPLICATIONINSIGHTS_CONNECTION_STRING`, exactly like the tracing exporter it mirrors, so it is a
local no-op until B1 activates it.

## Decision

**Error-content rule and its channel split.** A user-facing error message may carry money amounts,
dates and periods, and human-entered names — never a raw identifier (GUID), an account code, a
snake_case database/column name, a C# type name, or any other internal implementation detail.
Diagnostic values that used to live in the message move to typed properties on the exception instead
(`AccountingDomainExceptions.cs` gained `Kind`/`Reason`/`Basis`/`AccountCode`/`Amount`/`TenantId`/
`OwnerId`-shaped properties across its 16 concrete types), which the handler logs server-side but
never serializes to the wire. This is enforced, not just documented, for the Accounting module:
`DomainExceptionMessageTests` (`tests/LeaseBook.Tests.Accounting`) reflectively constructs one
instance per concrete `AccountingDomainException` type — iterating every value of a discriminator
enum so each message branch is covered — and fails if any rendered `.Message` contains a sample GUID,
a sample account code, a snake_case-shaped token, or a `…Exception|Service|Handler|Strategy` type
name.
A blanket "no identifiers ever" rule would break working behavior, so the rule has a second channel:
where a caller genuinely needs a structured value for **behavior**, not display, it travels as a
named, allowlisted `ProblemDetails.extensions` entry instead of prose. Two are allowlisted today:
`existingEntryId` on `duplicate_source_ref` (`LedgerComposer.tsx` reads it to treat a double-submit
as success — the P54 idempotency contract) and `verificationId`/`varianceTotal`/`clearingCash`/
`clearingAccrual` on `not_tied` (`VerificationStep.tsx` renders the specific variance). Conversely,
`ownerId` was deliberately dropped from `statement_not_balanced`'s extensions
(`ReportingEndpoints.cs`) — no client behavior read it, only `year`/`month`/`variance` do, so it had
no reason to be on the wire at all. The allowlist is a property of the specific error code, not a
general escape hatch.

**Correlation-id contract and the bare-404 boundary.** `ProblemResults.CorrelationId(HttpContext)`
returns `Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier` — the W3C trace id,
which is the same value Application Insights indexes as `operation_Id`. Using the ambient trace id
rather than minting a fresh GUID means the string an operator reads on screen is directly searchable
by an engineer with no separate correlation table or mapping step. Every response built through
`ProblemResults` stamps this as `correlationId` alongside `code`. The boundary is deliberate: bare,
body-less `TypedResults.NotFound()` / `Results.NotFound()` responses are excluded. They are
REST-conventional null-lookup results (13 sites across Directory/Settings and elsewhere), the
frontend already maps them to a generic message, RLS makes a cross-org id indistinguishable from a
nonexistent one so there is no existence oracle to protect either way, and wrapping them in a body
would add a payload nobody reads. A bug report about one of these is "the id doesn't resolve," not
"look at this specific request's trace" — if a real support scenario ever needs a reference on a bare
404, that is the trigger to revisit this boundary, not a reason to add one speculatively now.

**Single-factory rule, its enforcement, and the SharedKernel placement.** `ProblemResults`
(`src/LeaseBook.SharedKernel/Endpoints/ProblemResults.cs`) is the only code allowed to build a
`ProblemDetails` response: `Problem`/`TypedProblem` merge caller-supplied extensions with the stamped
`code` and `correlationId`, and `ValidationProblem` wraps `Results.ValidationProblem` the same way for
the two live validation-400 emitters (`ValidationExceptionHandler` for CQRS slices,
`ValidationEndpointFilter` for the auth DTOs). This is enforced by
`ErrorContractTests.Only_ProblemResults_builds_problem_details_responses`
(`tests/LeaseBook.Tests.Architecture/ErrorContractTests.cs`), which reads method references out of
the **compiled IL** of every application assembly — `SharedKernel`, the host, the `Migrator` and all
seven modules (`ArchitectureAssemblies.Application`) — and fails on any
`Results`/`TypedResults` `Problem`/`ValidationProblem` reference whose calling type is not
`ProblemResults` itself. A doc-comment convention was tried first and did not hold: the 28
pre-existing direct call sites (the same figure cited in Context) are the proof, echoing the lesson
ADR-012 and ADR-024 already drew for the generated API client and the changelog — a convention
without a gate rots.
The guard also asserts that a sanctioned call **inside** `ProblemResults` is still visible to it,
so it cannot pass because the IL reader or the target pattern stopped seeing its own subject — a
guard that can no longer go red is indistinguishable from one that is satisfied.
The limitation that remains is structural rather than syntactic, and it is the one the 2026-09-10
addendum (2) below was written about: **IL records only calls that were actually made,** so an error
response written by middleware, by a framework event, or by hand onto `HttpResponse` makes no
`Problem` call for the guard to find and is invisible to it at any scan fidelity.
`ProblemResults` lives in `LeaseBook.SharedKernel.Endpoints`, not the `LeaseBook.Web` host, even
though most call sites are host endpoint files. The reason is a module-boundary constraint, not a
preference: module endpoint files are themselves emitters.
`src/LeaseBook.Modules.Directory/Endpoints/SettingsEndpoints.cs` builds a `bank_account_has_uncleared`
problem response directly (the deactivate-with-uncleared-items conflict on
`PUT /banks/{id}/active`), and a module may reference `SharedKernel` only, never the host
(`ModuleBoundaryTests`, AGENTS.md). Putting the factory in `LeaseBook.Web` would have forced either a
module-boundary violation or a second, module-local copy of the same stamping logic. `IEndpointModule`
(`SharedKernel.Endpoints.IEndpointModule`) already established this precedent: it is the shared
endpoint-registration contract every module **and** the host implement, living in `SharedKernel`
specifically so both sides can reach it. `ProblemResults` follows that established shape rather than
inventing a new one.

**Log-level taxonomy and the accepted decorator double-logging.** `LogEvents`
(`src/LeaseBook.Web/Observability/LogEvents.cs`) defines stable, numbered `EventId`s — 1000
`UnhandledException`, 1001 `DomainRejection`, 1002 `ValidationRejection`, 1003 `ImportRowFailed`,
with 1000-1099 reserved for host/error plumbing and 1100+ for future domain areas — never renumbered
once assigned, since Track B's B4 alert rules key on them. Levels follow one rule: `Error` means
unexpected — something worth paging on, always with the exception object attached
(`UnhandledExceptionHandler`, `EntityImportService`'s per-row catch); `Warning` means an expected,
typed rejection the domain deliberately raised — a business rule declined the request, not a defect
(`AccountingExceptionHandler`, `ValidationExceptionHandler`). This taxonomy pre-dates this WP for one
layer: the CQRS pipeline decorators (`TelemetryCommandDecorator`/`TelemetryQueryDecorator`,
`SharedKernel.Cqrs.Decorators`) already log **every** handler failure at `Warning` with the exception
attached, before it reaches the exception-handler layer at all. That means an expected domain
rejection is now logged twice — once generically by the decorator ("Command X failed after Yms"),
once specifically by `AccountingExceptionHandler` ("Domain rejection {Code} mapped to {Status}
for…") — and an unexpected failure is logged at `Warning` by the decorator and again at `Error` by
the terminal handler. This WP accepts the duplication rather than narrowing the decorator's scope:
the decorator is upstream, cross-cutting infrastructure shared by every command and query in the
system, not just Accounting's, and changing what it logs is a separate, riskier change than this WP's
brief covers. It is an accepted cost, not an oversight — tracked below as a Follow-up — and it is
low-risk noise rather than a signal hazard because only the new, specific logs carry a `LogEvents` id
for B4 to key on.

**Provider-scoped EF export filter.** `Program.cs` registers
`builder.Logging.AddFilter<OpenTelemetryLoggerProvider>("Microsoft.EntityFrameworkCore",
LogLevel.Warning)` against the `OpenTelemetryLoggerProvider` type specifically, not globally.
`appsettings.json`/`appsettings.Development.json` filter only the `Microsoft.AspNetCore` category;
`Default` stays at `Information`. Without a provider-scoped filter, the moment logging routes to
OpenTelemetry, EF Core's per-query `Executed DbCommand` Information logs would all export to
Application Insights — noisy and costly at real traffic volume. Because the filter is scoped to the
export provider rather than the category globally, the local console logger is untouched: `dotnet
run` still prints SQL exactly as it did before this WP.

**The one deliberate status change: `no_trust_account`, 500 → 409.** This WP is not a status-code
audit — every other response keeps the status it had. `BankAccountInfoAdapter.GetOperatingTrustAsync`
previously threw a plain `InvalidOperationException` when no active trust bank account existed. That
type is not an `AccountingDomainException`, so the typed handler never saw it; it fell straight
through to a generic, uncoded 500, and after this WP's terminal handler lands, a raw
`InvalidOperationException` message would have been suppressed entirely — turning a recoverable
"create a trust account first" situation into an opaque failure. This WP adds
`NoTrustAccountException : AccountingDomainException("no_trust_account", …)` (a new, GUID-free typed
exception) and raises it from the same site. `AccountingExceptionHandler`'s status switch maps every
code it does not explicitly list to 409 via its default arm, so `no_trust_account` now resolves to
409 Conflict — a deliberate reclassification from "unexpected server failure" to "a conflict the
operator can resolve," which is what the situation always was.

**Rejected alternative: `UseAzureMonitor()`.** `Azure.Monitor.OpenTelemetry.AspNetCore`'s
`UseAzureMonitor()` is a single call that wires tracing, metrics, and logging together with the Azure
Monitor exporter, reading the connection string itself. It was rejected for this WP.
`Program.cs`'s tracing pipeline already existed before this WP as a hand-assembled
`AddOpenTelemetry().WithTracing(...)` registration with a custom `LeaseBookTelemetry.Source`
`ActivitySource`, `AddAspNetCoreInstrumentation()`, and an exporter gated behind the same
`APPLICATIONINSIGHTS_CONNECTION_STRING` presence check this WP reuses for logs. Adopting
`UseAzureMonitor()` would have meant replacing that already-accepted, fine-grained pipeline rather
than extending it, would have silently turned on metrics collection this WP does not ask for, and
would have left less room for the provider-scoped EF filter above (a single umbrella call configures
the logging provider itself). Extending the existing piecemeal pipeline with
`builder.Logging.AddOpenTelemetry(...)` + `AddAzureMonitorLogExporter(...)` keeps one consistent
wiring style for traces and logs alike and preserves that control. Revisit if the metrics half of
`UseAzureMonitor()` is ever wanted (a candidate for B4).

### 2026-08-12 amendment — distro re-evaluation

Issue #163 reopened this choice against the current Azure Monitor distro rather than carrying the
2026-07 conclusion forward by assumption. The result is **defer again; keep the standalone exporter
and the manual pipeline**. The supporting comparison and primary-source links are captured in the
[Azure Monitor distro evaluation](../research/azure-monitor-opentelemetry-distro.md).

The distro is now more capable, but adopting it is not an exporter-only substitution. It adds
ASP.NET Core, HTTP client, and SQL client tracing; server/client and Application Insights standard
metrics; Azure resource detectors; logging; rate-limited trace sampling; trace-based log sampling;
and Live Metrics. LeaseBook's current production-telemetry scope needs the existing request/custom
traces, correlated structured logs, click-budget queries, and alert events. It does not yet require
OpenTelemetry meter instruments or Live Metrics, so enabling those defaults would broaden collection
before an operator has a live requirement or a measured cost/volume baseline.

The original control concerns are no longer absolute blockers. The custom
`LeaseBookTelemetry.SourceName` can be added after `UseAzureMonitor()`, the service resource can be
configured, and the EF Core rule can remain scoped to `OpenTelemetryLoggerProvider`. The distro and
standalone paths both use the Azure Monitor exporter, so retry/offline-storage behavior and optional
Microsoft Entra credentials are available either way. Those capabilities therefore do not justify a
pipeline migration on their own.

The security default is the deciding risk. Microsoft's distro currently disables ASP.NET Core and
HTTP-client query-string redaction unless the corresponding
`OTEL_DOTNET_EXPERIMENTAL_*_DISABLE_URL_QUERY_REDACTION` settings are explicitly set to `false`.
LeaseBook's standalone ASP.NET Core instrumentation redacts query values by default, and
`DeliverTelemetryTests` re-verified that the recipient email on the statement-delivery query string
does not reach Activity tags during this evaluation. A distro migration would therefore require an
explicit privacy override plus regression coverage in the same package change; inheriting the
distro default is not acceptable.

Operationally, nothing changes in this amendment: exporters remain conditional on a non-empty
`APPLICATIONINSIGHTS_CONNECTION_STRING`; the custom ActivitySource, W3C operation correlation,
structured-log options, and provider-only EF filter remain intact; no metrics provider or Live
Metrics channel is added. This gate is load-bearing: an unconditional `UseAzureMonitor()`
registration reaches exporter construction and throws when it cannot resolve a connection string,
instead of preserving the current local/test no-export state. Revisit only when OpenTelemetry metrics
or Live Metrics become an explicit release requirement and a live Application Insights environment
exists to validate them. Any future adoption must make query redaction explicit, keep the distro
registration behind the connection-string gate, retain the custom source and EF filter, choose
sampling and offline-storage policy deliberately, and run the telemetry security suite before
deployment.

### 2026-08-20 amendment — the SPA error contract reaches read paths

This ADR's frontend half consolidated five ProblemDetails **mappers** into one. It did not consolidate
the **success rule** that decides when a mapper runs at all, and that gap quietly reintroduced the
problem the consolidation set out to solve.

Twenty-one read sites across eight files each wrote `if (error || !data) throw new Error('<literal>')`.
Twenty of them discarded the ProblemDetails body entirely and substituted a hardcoded string, so the
`code` and `correlationId` this ADR guarantees on every response never reached the SPA — and
`ApiErrorNotice`, built here to render `Reference: <32-hex>`, had never once been fed by a failed
read. The contract held on the wire and was thrown away on arrival. Nothing was red, because a
hand-written throw is not a defect at any individual site; only the aggregate was.

**What changed.**

- `unwrap(call, fallbackMessage)` in `web/src/api/request.ts` is now the one success rule. It throws
  an `ApiError` built by `toApiError`, so `code` and `correlationId` survive on read paths as they
  already did on writes. The site's former literal survives as `fallbackMessage` and is used only when
  the server body carries no `detail`, `title`, or validation entry — "Failed to load the register"
  is better copy than `Request failed (500).` on an empty body, and worse than the server's own
  explanation when there is one. Three modules (banking, onboarding, operations) had independently
  converged on a byte-identical private `unwrap`; those three are now this one.
- `ApiError` gained `status`. Domain copy sometimes keys off status rather than a body code — an
  empty 404 still means "not found" — and without it that mapping could only live in the transport
  layer, which is what kept `compliancePackError` there.
- `download(call, filename, fallbackMessage)` owns the blob-to-anchor tail that stood at five sites
  with four different error contracts above it. It is deliberately the only export: a lower-level
  `download(blob, filename)` is what would let those four contracts grow back, and no caller needs it.
- `lib/apiError.ts` moved to `web/src/api/apiError.ts`. The old split — transport in `api/`, error
  vocabulary in `lib/` — is what let `lib/telemetry.ts` re-implement transport policy in `lib/`
  without anything saying otherwise. `@/api` is now one import site for everything about talking to
  the host.
- `lib/telemetry.ts` uses the generated `postApiTelemetryBudget` instead of a raw `fetch` with a
  hand-rolled `readCookie` and `X-XSRF-TOKEN` header. Its justifying comment claimed the budget
  endpoint was host-owned and therefore had no generated function; it is in `sdk.gen.ts` and always was. The
  argument is not deduplication at n=1 — it is that a second copy of _security_ policy works today
  and fails silently forever if the XSRF contract moves. The fire-and-forget swallow is kept: a
  UX-metrics ping that can surface errors into the UI is worse than one that cannot.
- `compliancePackError` retired to `CompliancePackPanel.tsx`. It was the last bespoke survivor of the
  five-way consolidation and the outlier: four other surfaces already key friendly copy off
  `err.code` in the component. Report-specific wording does not belong in a transport module. Its
  branch order is preserved exactly, including status-wins-over-code for 422.
- The four vestigial aliases (`toBankingError`, `toOnboardingError`, `toRunError`, `toReportsError`,
  all bare `= toApiError`) are deleted.

**Enforced, not conventional.** `SpaRequestExecutionTests` fails the build when the success rule,
`createObjectURL`, a `document.cookie` read, or a raw `fetch(` appears anywhere under `web/src`
outside `web/src/api`. It is source-scanned because TypeScript offers the C# guards no IL, and it
carries a vacuity control asserting the api module's own occurrences are still found — a rename that
turned the scan green would otherwise read as compliance. `RepositorySource.WebSourceFiles()` is
deliberately separate from `CodeFilesUnder` so adding TypeScript here cannot widen what the C#/SQL
guards scan.

**Known limit, stated rather than implied.** The mechanism now carries `detail` and `correlationId` to
every read failure, and the surfaces that already render `ApiErrorNotice` show them — the banking
import wizard's match preview is covered end to end by test. Roughly 28 other read-error branches
across 18 components still render a hardcoded `EmptyState` description and discard `query.error`
before it reaches the DOM. Wiring those is a UX decision (does every error empty-state grow a support
reference, and what does it look like?), not a transport one, so it is deliberately not in this
change. The data is there when that decision is made.

**Residual holes — stated so "nothing fails silently" is an honest claim, not overstated:**

- **Streamed responses.** Every current file download (`/api/reports/{id}/csv`,
  `/api/statements/{ownerId}/pdf`, `/api/statements/{ownerId}/csv`,
  `/api/reports/compliance-pack`) builds the full `byte[]` in memory before calling
  `Results.File(bytes, …)`, so an assembly failure today still happens before any response byte is
  written and is still caught by the normal exception-handler pipeline. If a response is ever changed
  to genuinely stream — writing to `HttpResponse.Body` as it is produced — a failure after the first
  flush could not be converted to a `ProblemDetails` body: the status and headers are already
  committed, so the client would see a truncated file with no error surface, and only the log-side
  correlation id would exist. Not a live gap today; a boundary to remember if a download is ever
  converted to true streaming.
- **CLI verbs.** `seed`, `check-invariants`, `perf-probe`, and the other
  `dotnet run --project src/LeaseBook.Web -- <verb>` commands dispatch and return before
  `app.Run()` — entirely outside the ASP.NET Core request pipeline. There is no `HttpContext` for
  `ProblemResults.CorrelationId` to read and no `IExceptionHandler` in the path; their `ILogger`
  output does flow through the new OpenTelemetry pipeline, but an unhandled CLI failure is a plain
  process failure with an exit code, not a correlated error. Acceptable today because these are
  operator-run, synchronous, and loud; this ADR's contract is HTTP-surface only.
- **The WP-11 Hangfire-wrapper obligation.** WP-11 (nightly trust-invariant sweep via Hangfire,
  ADR-001's first scheduler integration) has not started as of this WP. When it runs `InvariantSweep`
  as a background job, that job executes with no HTTP request and therefore no correlation id to
  stamp — but its failure/violation path should still emit through this same `LogEvents` taxonomy (a
  new id in the reserved 1100+ range) rather than inventing its own. Recorded here so WP-11 inherits
  this contract explicitly instead of designing its error handling independently.

### 2026-08-21 amendment — XSRF priming absorbed into the request path

The 2026-08-20 amendment consolidated the success rule, the download tail, and the error vocabulary
into `web/src/api`, and left one request-execution policy outstanding (recorded as issue #237):
25 manual `await primeCsrf()` calls across seven feature files, each a precondition the author of a
new mutation had to remember, and each failing as an antiforgery rejection rather than anything
visible at the call site — the same shape as the three policies already absorbed.

**Why the calls existed.** The antiforgery request token is user-bound: a token minted signed-out is
invalid once signed in, and vice versa. Priming before every mutation guaranteed freshness by brute
force — at the cost of a serial `GET /api/auth/csrf` round-trip inside every mutation.

**What changed.** The client interceptors in `web/src/api/client.ts` now own token freshness in two
layers, and all 25 call-site primes are deleted:

- **Prime on demand.** The request interceptor, on an unsafe method with no readable `XSRF-TOKEN`
  cookie (first-ever mutation, or the session cookie evicted while the 8-hour auth cookie lives on),
  fetches a token before sending. Concurrent unprimed mutations share one refresh. Best-effort: a
  failed prime lets the request proceed to the server's verdict.
- **Replay once on rejection.** Cookie presence cannot detect a _stale_ token — present but minted
  for a different signed-in state. The server can: `ApiAntiforgeryMiddleware` rejects with the stable
  `antiforgery_rejected` code this ADR's contract guarantees. On that code (and only that code) the
  response interceptor refreshes the token and replays the request exactly once; a second rejection
  surfaces as the error it is. A refreshed token identical to the one already sent is treated as
  "not staleness" and not replayed — a mutation is not something to repeat on a guess. This is the
  first consumer of the machine-readable `code` inside the transport layer itself, which is why the
  mechanism lives here.

  **The replay must never be built with `Request.clone()`,** and the first attempt at this was.
  Cloning tees the body into a `ReadableStream`, which mutates the _original_ request as much as the
  copy: the browser then sends every mutation as a chunked upload with no retrievable body. Nothing
  functional broke — the server still parsed the body, and 76 of 79 e2e specs passed — but
  `request.postData()` went `null`, which is how the two budget-telemetry specs
  (`keyboard-only.spec.ts`, `m3-ledger.spec.ts`) caught it. Those two specs are the regression guard
  here; the failure is invisible to the unit layer, because MSW reads a tee'd stream perfectly well.
  The replay instead rebuilds its request from the client's own resolved `options`, through the same
  `getValidRequestBody` the client used to build the original, and issues it through the resolved
  fetch — bypassing the interceptor chain, so it cannot replay again. Nothing consumes or tees the
  in-flight request.

`primeCsrf` remains exported for auth-state changes only — `LoginPage` primes on mount (refreshing
the anonymous token) and after sign-in (refreshing the user-bound one), which is latency hiding on
the sign-in path, not correctness; the interceptors are the correctness. The sign-out path carries
no manual prime at all. Mutations get one round-trip faster in the common case, and the
"remember to prime" precondition is gone rather than renamed — the outcome #237 required.

### 2026-09-09 amendment — auxiliary reads render their own failure

The 2026-08-20 amendment left one question open on purpose: "does every error empty-state grow a
support reference, and what does it look like?" Issue #302 and its children answered it, so the
answer is recorded here rather than left to be re-derived per surface.

**The decision.** A failed read renders `ApiErrorNotice` plus a retry control, in place of the value
the read would have produced — not a hardcoded `EmptyState` description, and not the value's
fallback.

**The defect class it closes.** The 08-20 amendment framed the gap as lost `code`/`correlationId`.
The children of #302 found the larger cost: a read has **three** outcomes, not two — pending, failed,
and succeeded-with-nothing — and only the third is a fact about the organization. Every site that
collapsed the first two into the third rendered a failure as a confirmed answer:

- `LeaseLateFeeModal` seeded each override control from `Number(org?.x ?? fallback)`. With
  `/api/settings/org` failing, switching a field to "Override" wrote invented values — day 1, grace
  0, `$0` — and Save persisted them onto the lease as deliberate policy.
- `ReconciliationHistory` read `history.data?.length ?? 0` and reported a confirmed
  "0 reconciliations" while pending or failed. On an audit trail that is the difference between
  "nothing was finalized" and "we could not check".
- `ApplyModal` and `LedgerComposer` resolved trust banks out of `banks.data` and told the operator
  "No trust bank is configured for this org" when the bank list simply had not loaded.
- `NewPropertyModal` reported an org with no owners; the reports `SelectChip` popover offered a bare
  "All"; `BankingPage`'s register filter offered every property as an em dash; `DashboardPage` took
  the operational-org branch of both onboarding takeover rules, so a failed status read silently
  produced no redirect, no migration banner, and no explanation.

An error rendered as a confirmed value is worse than a visible error, because it is actionable: the
operator acts on it, and in the late-fee case the action was a durable money-affecting write.

**Two rules follow.** A surface that posts money blocks the write while a prerequisite read is
unavailable — including a failed refetch still holding stale rows, since a stale bank list may name
a since-deactivated account. And an `unwrap` fallback is user-visible copy, so it is a sentence: the
nine `directory.ts` fallbacks that read `'owners'`, `'tenant create'` and the like became real
sentences once the contract routed them to `ApiErrorNotice`.

**Residual, measured rather than estimated.** The 08-20 amendment counted ~28 branches across 18
components. #302 scoped itself to auxiliary reads: the ones feeding a selector, a label, a count, or
an enable/disable decision, where the failure is invisible. The remainder are primary-content
regions, where an error at least occupies the space the content would have. This decision binds them
too, and #349 converted them — see the addendum below for what that turned up.

### 2026-09-10 addendum — the primary-content conversion, and what it cost to find

Issue #349 converted the primary-content regions. The findings below are worth keeping, because each
one is a thing the next audit of this kind will otherwise repeat.

**The count was 19 branches, not nine surfaces, and it took two passes to establish that.** #349
named nine surfaces and supplied a recipe to re-derive them: `rg -l 'icon="alert"'` minus the files
that already render `ApiErrorNotice`. That subtraction is wrong at the file level — a file converted
on one surface still hides unconverted ones — and it masked eight more branches, all on money
surfaces: `DashboardPage`, `BankingPage`'s bank-balances and register reads, both `ReportCatalog`
reads, and the rent, late-fee and disbursement run previews. Counting by branch also split one of
the nine in two, since `LedgerPage` carried the tenant-header read and the ledger body separately —
so the nine named surfaces were already ten branches before the eight were added.

Auditing by branch rather than by file found those eight. It did **not** find the nineteenth, which
pre-merge review did: `SettingsPage`'s org-settings read rendered a bare string in a `Card`, not an
`EmptyState` — a second branch inside a file the original list already named. Both recipes keyed off
`EmptyState`, so neither could see it. The lesson is narrower
than "audit by branch": an audit that greps for the _shape_ of the wrong answer only ever finds the
instances that happened to choose that shape. Enumerate the population instead — every `isError`
branch — and check each against the contract.

**A private `unwrap` in `lib/directory.ts` made the whole Directory conversion inert.** It shadowed
the shared one from `@/api` and threw a plain `Error` reading `Failed to load <what>`, discarding
`code`, `correlationId` and `status` before any component saw them — so every tenant, owner and property
read produced a fabricated message with no reference, and the conversion would have rendered that
fabrication more prominently. `request.ts` records that three modules had each grown a private
`unwrap` and were consolidated; this was a fourth that the consolidation missed. Its call sites
passed sentences already, so they became correct `fallbackMessage`s unchanged.

**`OnboardingPage`'s error branch was unreachable.** Its loading guard read
`isPending || activeStep === null`, and `activeStep` is only ever seeded from a _successful_ status
read — so a failed read rendered a permanently animating skeleton and never reached the error copy
below it. This is the `isPending`-on-a-disabled-query trap in a second shape: any loading guard
`or`-ed with a condition that a failure cannot clear swallows the error branch. The error branch now
precedes the loading guard.

**And it made five mutation sites correct as a side effect.** The New tenant, New owner and New
property modals each caught the create failure and replaced it with "Check the fields and try
again" — advice that is wrong for a 409 or a 503, and that discarded the reference. `SettingsPage`'s
two save forms did the same with "Couldn't save. You may need admin rights.", guessing a cause the
server had already stated. Before the
`directory.ts` fix there was no reference to keep; after it there was, one line above where it was
being thrown away. The three now render `ApiErrorNotice`, with the form's own validation copy kept
as a plain string in the same slot, so a local "Choose an owner for this property" and a server
rejection stay distinguishable.

**No mutation residual — and the last two exposed a third private success rule.** Documentation
review found two write branches still answering a rejection with copy of their own: `SettingsPage`'s
`NewBankModal` ("Could not create the account. Check the fields and try again.") and
`LeaseLateFeeModal` ("Couldn't save the lease overrides."). Both are converted, which makes seven
mutation sites in total.

Converting the second one is what surfaced `useUpdateLease`: it did `if (error) throw error`,
throwing the raw problem body rather than mapping it. The rejection therefore reached the UI with no
`message` and no `status` at all, so the surface silently fell back to its own copy while the
server's reason sat unread inside the thrown object — the test asserting the server's message failed
even after the component was correct. That is the same defect as the private `unwrap` above —
the second hand-rolled success rule found in `directory.ts` alone. It now goes through the shared `unwrap` with
`allowNoContent`.

`LoginPage`'s "Invalid email or password." is **not** in that set and must stay as it is: a
deliberate uniform answer that refuses to distinguish an unknown account from a wrong password.
`rg 'if \(error\) throw error'` and `rg 'isError && <span className="err"'` are both now empty
outside the generated client.

**And the first test to actually drive a rejection found four unhandled ones.** Four handlers
awaited `mutateAsync` with no `catch`: `LeaseLateFeeModal`, both `SettingsPage` save forms, and
`VerificationStep`'s verify. React discards the promise an async click or submit handler returns, so
each rejection escaped as an unhandled rejection. It was invisible while the surfaces answered with
their own copy, because the copy is driven by `isError` rather than by the promise — the UI looked
handled while nothing handled the rejection. Vitest reports it as `Errors 1 error` alongside a fully
green `Tests` line and exits non-zero, so a `grep` for the test counts alone reports success on a
red run: check the exit code, not the summary.

**The `internal_error` copy was false on every read, and no test could see it.** `ApiErrorNotice`
replaced the message with "Something went wrong on our end. Nothing was saved." whenever
`code === 'internal_error'` — and `UnhandledExceptionHandler` stamps exactly that code on every
unhandled exception, so it is what a production 500 produces. Harmless on a mutation, which is what
the copy was written for; false on a read, and this conversion is what would have scaled it to
nineteen whole-page content regions. `ApiErrorNotice` now takes `kind`, defaulting to `'write'` so
no existing mutation site changes, and a read says "Something went wrong on our end." without the
claim about saving. A file download counts as a read.

Why no test caught it is the more useful half. The e2e `routeFail` helper sent
`{ title: 'Internal Server Error', detail }` with no `code`, and `toApiError` derives `code` from
`title` when the body omits it — so the fixture produced `code: 'Internal Server Error'` and never
entered the branch. Every unit test used a 503-with-`detail` or a bodiless 500. The suite exercised
the contract against a shape the server does not emit. A fault-injection fixture is itself a claim
about the server, and it has to be checked against the server's own error factory.

`routeFail` still omits `code`, deliberately, and that is worth stating so it is not "fixed" later:
adding `code: 'internal_error'` would make `ApiErrorNotice` replace the message with the generic
copy, and every spec in `error-states.spec.ts` asserts on `ROUTE_FAIL_DETAIL` — the helper models a
_typed_ 500 that explained itself, not an unhandled exception. #349 added the `correlationId` it had
been missing, so the e2e now proves the reference survives transport; the `internal_error` copy is
covered at the unit layer instead, where both `kind`s can be asserted directly.

**What the conversion added.** `QueryErrorState` (`web/src/components/`) is the block-level form of
the contract — `ApiErrorNotice` and a retry inside `EmptyState`'s layout — so a converted surface
keeps its existing shape and heading and changes only what the description says. `isNotFound` in
`web/src/api/apiError.ts` tells a failed read apart from a 404 on a detail route, which is a
successful answer to a wrong id: those keep "Record not found" and offer no retry, since retrying
reproduces the 404. `EmptyState` is now reserved for the third outcome only.

**Not mechanically enforced, and it cannot be by the same means.**
`SpaRequestExecutionTests` guards the transport rule because a hand-written success rule is a
grep-able shape. "This component rendered a failed read as a confirmed value" is not: the defect is
the absence of a branch, and a missing branch has no syntax. Coverage here is a per-surface test
asserting the error state — 23 of them on this change, plus 31 more on #349 — so a new read is
guarded only if its author writes one. #349 added the sharper rule: a test that asserts only the
error _heading_ is not coverage, because the heading survives the regression. Assert the mapped
message and `Reference: <id>`, and confirm the test goes red with the branch reverted.

### 2026-09-10 addendum (2) — the one 401 that carried no contract

Issue #357. Converting the primary-content branches (#349) made a pre-existing dead end visible
rather than creating one: a session that expires with the tab open now offered a **Retry**, and that
Retry re-issues the same read, gets the same 401, and never resolves. `RouteGuard`
(`web/src/app/RouteGuard.tsx`) decides authentication solely from the cached `useSession` query, and
nothing invalidated it when a _different_ request came back 401, so the operator was told the
register could not load when the truth was that they were signed out.

**The 401 carried no contract at all, and this ADR's own guard could not see that.**
`ConfigureApplicationCookie`'s `OnRedirectToLogin` wrote a bare status code for `/api` paths
(`AuthServiceCollectionExtensions.cs`), and the pipeline has no `UseStatusCodePages` to dress it —
so the single most common error a signed-in operator meets was the one error response in the product
with neither a `code` nor a `correlationId`. `ErrorContractTests` scans IL for direct
`Results.Problem` / `TypedResults.Problem` calls outside `ProblemResults`; this path never made one,
so there was nothing for it to catch. **A single-factory guard proves that the responses built by the
factory are well-formed. It says nothing about error responses written by hand, by middleware, or by
a framework event** — and those are exactly where the contract goes missing. The cookie handler's
401 now goes through `ProblemResults.TypedProblem` and carries `not_authenticated` plus the
correlation id.

`OnRedirectToAccessDenied`'s 403 is deliberately still bare. `AuthorizationMatrixTests` asserts that
a role denial is _not_ `application/problem+json`, which is what pins the MFA result handler's
problem response to MFA alone. Extending the fix to that branch would have silently voided that
test's meaning; it is a decision to make on its own evidence, not a tidy-up.

**Status alone does not mean "signed out", and a rule keyed on it would have shipped a lie.** Eleven
call sites across seven auth endpoints answer 401 with ProblemDetails, and three of the five codes
they use — `invalid_credentials`, `invalid_mfa_code`, `invalid_recovery_code` — mean _the credential
you just supplied was rejected_, not _your session ended_. `isSessionExpired`
(`web/src/api/apiError.ts`) is therefore an **allowlist** of the two signed-out shapes (no `code`, or
`not_authenticated`), not a denylist of the credential codes: a 401 code added later then
defaults to showing the server's own message, which is merely unhelpful, rather than to a confident
"you have been signed out", which would be wrong. The allowlist's prediction came true within the
month: `mfa_session_expired` (#360) is exactly that later 401 code, it falls outside
`isSessionExpired`, and it correctly renders the server's own message instead of a confident claim
about the session.

This is invisible on the login page, which renders one generic literal for every rejected
credential. That is **a defense, not an accident** — corrected 2026-09-11, and it contradicted this
ADR's own addendum (1) above ("a deliberate uniform answer that refuses to distinguish an unknown
account from a wrong password... must stay as it is") on the day it was written. See the 2026-09-11
amendment below, which makes the divergence explicit rather than incidental.

**The remedy is honest copy, not an automatic redirect.** Redirecting on a signed-out 401 was
rejected because it acts on the operator's behalf at the moment they have the least information: a
bulk-run screen holds its previewed run and period in component state (`RentRunScreen.tsx`), and
unmounting it discards their confirmed selection and leaves them at `/login` with no account of why
the screen vanished. Be precise about what this buys, because it is easy to overstate: signing in
still costs them that preview — the anchor is a full document load, and `/login` has no return path
— so what the choice preserves is not the work but the _explanation_, and the timing. They read what
happened and decide when to leave, instead of finding themselves somewhere else. Instead
`ApiErrorNotice` renders the signed-out copy — split on `kind`, so a rejected **write** still gets
the "nothing was saved" reassurance that a read must not claim.

**Fixing this on the block-level surface alone would have made things worse.** `QueryErrorState` was
the obvious place, and stopping there left the seven auxiliary reads — the bank-account selector, the
owner list on the property form, the report chips, the dashboard's migration banner — each
hand-rolling the same ghost Retry beside their own `ApiErrorNotice`. Those would have read "You have
been signed out" next to a button that could not act on it: copy contradicting affordance, which is
worse than the uninformative copy it replaced, because the operator now believes the button is
relevant. Pre-merge review caught this; the static reasoning that produced the fix did not, because
the fix looked complete from the component it was written in. The affordance is now one component,
`ErrorAction`, for the same reason `ProblemResults` and `unwrap` are one thing each: eight copies of
a decision drift the moment the decision changes, and this decision just changed. It also pins the
one anchor that wears the button shape to `buttonClassName` rather than a copied literal.

**The same review found a second thing the #349 sweep had missed.** `CompliancePackPanel` rendered a
failed compliance-pack _download_ with the default `kind`, so the operator was told "Nothing was
saved" about a ZIP that was never saving — the exact category error the 2026-08-20 and 2026-09-10
amendments fixed for `internal_error`, still live on a surface the sweep had walked past. It was the
only read among the `ApiErrorNotice` call sites left without an explicit `kind`; every other one is a
genuine mutation. This is the nineteenth branch's lesson in a second costume: the sweep enumerated
the branches that _look like_ a failed read, and a download does not, until you ask what the
operation was doing. Enumerate by what the operation **is**, not by what its call site resembles.

**The regression test has to hold one read succeeding while another fails.** The defect lives in the
state where `/api/auth/me` is still cached as signed-in _and_ a data read returns 401; failing both
is a different, already-handled state in which `RouteGuard` redirects correctly. `sessionExpiry.test.tsx`
therefore asserts its own premise — that nothing redirected — before asserting the copy, so the test
cannot quietly start passing for the wrong reason if the guard's behavior moves.

### 2026-09-11 amendment — sign-in is a standing exception, and the axis it turns on

Issue #360. Two defects that only look separate. `POST /api/auth/mfa` answered `invalid_credentials`
when `GetTwoFactorAuthenticationUserAsync()` returned null — which is what an expired partial cookie
produces — so a user who took too long over their authenticator app was told their credentials were
wrong and advised to retype a code that was never the problem. And `LoginPage` replaced every failure
with its own literal, so that lie was invisible: a 500, a dropped connection, a rate limit and a
mistyped password all rendered identically, and a failed sign-in was the one operator failure in the
product producing no reference to quote.

**The expired partial session now has its own code, `mfa_session_expired`**, on both two-factor paths
(`/api/auth/mfa` and `/api/auth/mfa/recovery`). It is the fifth 401 code and the second that does not
mean _credential rejected_.

**Sign-in is a standing exception to "the UI renders what the contract carries," and the exception is
principled rather than incidental.** Rendering the server's `detail` on the password step would be an
account-enumeration regression: `AuthEndpoints` deliberately answers bad credentials, lockout and an
unknown email with one code and one message, because login must never reveal whether an email exists.
Addendum (2) above previously called this divergence "an accident... not a defense"; that was wrong
when written and is corrected there.

**The axis is not _which_ credential failed but _whether a credential was judged at all_.** These
endpoints judge one at exactly one status — **401**. Everything else is the request failing before or
around that judgement: a validation 400 describing the fields just submitted, `antiforgery_rejected`,
a 5xx, a rate limit, a dropped connection. None of those bodies can carry account-specific
information, so they render in full with their reference. That is what the first attempt got wrong:
it allowlisted four _conditions_ rather than keying on the status, which left every non-401 failure
falling through to "Invalid email or password." — telling an operator whose antiforgery token had gone
stale that their password was wrong, and looping them there indefinitely.

**The direction of the rule is the load-bearing part, and it is the same direction as
`isSessionExpired`.** The generic branch is keyed on 401, so a 401 code added later defaults to the
generic copy — merely unhelpful. The inverse, listing the credential codes and revealing everything
else, would default a _new credential code_ to rendering the server's detail on the password step,
which is the enumeration leak itself. Fail toward unhelpful, never toward wrong.

**An honest error must also leave an exit.** Telling an expired attempt to "start again from the
sign-in page" while stranding it on the code form — whose only control re-issues the same 401 — would
have been #357's dead end with the copy now naming the missing exit. The page returns to the password
step on that code, so the affordance matches the sentence. Pre-merge review caught this; the static
reasoning that produced the fix did not, for the third time in two changes, because the fix looked
complete from the component it was written in.

**The one thing sign-in cannot have is a reference on a rejected credential.** That is the deliberate
cost of the uniform answer: `detail` and `correlationId` are both withheld, so a "wrong password"
report is diagnosed by route and time window rather than by reference. `docs/runbooks/diagnostics.md`
records that, because it is the only operator-visible failure left in the product with no reference by
design.

## Consequences

- Every error response an operator can screenshot now carries a `Reference: <32-hex>` string they can
  quote verbatim in a support conversation, and the corresponding engineer query is a single
  Application Insights query (documented in `docs/runbooks/diagnostics.md`) — the loop this WP exists
  to close is closed for the HTTP surface.
- No reviewed exception message can leak an identifier, an account code, a database column/constraint
  name, or a .NET type name to a user — mechanically enforced for the Accounting module's 16 concrete
  exception types by `DomainExceptionMessageTests`; hand-reviewed but not yet mechanically enforced
  for host-project exceptions (see Follow-ups).
- A new problem-response call site cannot silently bypass the contract: `ErrorContractTests` fails the
  build the moment a new direct `Results.Problem`/`TypedResults.Problem`/`Results.ValidationProblem`
  call appears in any application assembly — at the cost of the one structural limitation named
  above: an error response that never makes such a call is not a call site it can see.
- Frontend error handling collapsed from five independently drifted, hand-rolled mappers to one
  (`web/src/api/apiError.ts` + `web/src/components/ApiErrorNotice.tsx`), fixing `reports.ts`'s
  silently-dropped validation branch as a side effect of consolidation rather than a separately scoped
  fix. Extended 2026-08-20 (see the amendment above): the success rule and the blob download joined
  it, so the contract now reaches read paths and is guarded by `SpaRequestExecutionTests` rather than
  by convention.
- Operators get an actionable 409 instead of an opaque 500 for the one reclassified case
  (`no_trust_account`); every other status code is unchanged, so this WP introduces no other
  behavioral surprise on the error path.
- Cost accepted: expected domain rejections are logged twice (decorator + handler) until the
  Follow-up below lands; the `ProblemResults`/`LogEvents` apparatus is a small amount of new
  host-composition surface a contributor must learn before adding a new error path, in exchange for
  that path being impossible to get wrong silently.

## Revisit trigger

Reopen this decision if any of the following happens:

- A response is changed to genuinely stream to `HttpResponse.Body` and needs mid-stream error
  reporting — the "streamed responses" hole must be resolved before that endpoint ships.
- WP-11's Hangfire sweep lands and needs its own log-event id or correlation strategy beyond what is
  recorded here — extend this ADR (or add a short addendum, per the ADR-016 precedent) rather than
  re-deriving the taxonomy independently.
- **Fired 2026-09-11, tracked by #361.** A second error response written outside the factory by
  middleware was the trigger to gate that surface behaviorally rather than extend a call-site scan
  that structurally cannot reach it. Auditing for #359 found it already in the tree:
  `MfaAuthorizationResultHandler` hand-builds the MFA-enrollment 403 with `WriteAsJsonAsync`, so it
  serves `application/problem+json` carrying neither `code` nor `correlationId`. It is a worse shape
  than the bare 401 was — a body-less response is at least honest that it has nothing to quote,
  whereas this one looks diagnosable and is not, and the SPA's `code ?? title` fallback silently
  promotes its English `Title` into the machine-readable slot. The remaining trigger is a **third**
  instance before #361 closes: that would make the pattern a habit rather than a pair, and the gate
  the urgent work rather than the fix.
- The decorator double-logging becomes a measurable signal-to-noise problem in Application Insights
  once B1/B4 are live, rather than a documented, accepted cost — promote the Follow-up below.

## Follow-ups

Recorded here so each has a durable landing place rather than being silently done or silently
dropped:

- **Decorator `Warning` reclassification.** `TelemetryCommandDecorator`/`TelemetryQueryDecorator`
  log every handler failure at `Warning`, including expected typed domain/validation rejections that
  the specific exception handler already logs with better structure at the same level. Teach the
  decorator to distinguish an expected, typed rejection from a genuinely unexpected one (skip or
  downgrade the former) so an expected rejection is not double-logged at `Warning`. This also retires
  the accepted double-logging above.
- **Explicit switch arms for `insufficient_receivable`/`no_trust_account`.**
  `AccountingExceptionHandler`'s status switch documents its `_ =>` 409 default arm with a comment
  listing the codes it covers (`period_closed`, `insufficient_liability`, `reserve_floor`,
  `already_reversed`, `duplicate_source_ref`, `account_period_locked`, `reconciliation_unbalanced`,
  `reconciliation_state`). `insufficient_receivable` and `no_trust_account` also resolve to 409
  through that same default arm — correctly, and pinned by `AccountingExceptionStatusTests`'s
  full-matrix Theory — but by construction rather than declaration, and absent from the comment. Add
  them explicitly so the mapping documents itself rather than relying on a reader to trust the
  fallback.
- **The import-row log-side assertion, blocked on an `ApiFactory` logger seam.** `EntityImportTests`
  proves the wire-side copy ("This row could not be imported. Check the values and try again."), but
  no test proves the raw exception detail actually reaches the `Error` log for `EntityImportService`.
  `UnhandledExceptionHandlerTests` substitutes a `CapturingLogger<T>` directly because that handler is
  trivially constructable; `EntityImportService` is resolved through the full DI graph via
  `ApiFactory` (`WebApplicationFactory`), which has no current seam to intercept or capture its
  logger. Add one (e.g. a swappable `ILoggerProvider` registered in `ApiFactory`) and assert the log
  side.
- **`EntityImportService` per-row catch logs expected validation failures at `Error`.** The per-row
  catch logs a `ValidationException` raised by command validation against a parseable-but-invalid row
  at `Error` under `ImportRowFailed` (1003) — the same level as a genuinely unexpected failure. Before
  Track B's B4 wires alerting to key on `Error` events, split the expected `ValidationException` case
  (`Warning`) from genuinely unexpected failures, so routine dirty-CSV imports don't page.
- **Leak-guard coverage for host-project exception messages.** `DomainExceptionMessageTests`
  reflectively covers every concrete `AccountingDomainException` — Accounting module only. Host-project
  exceptions (`MigrationNotTiedException` and others outside
  `LeaseBook.Modules.Accounting.Contracts`) are hand-reviewed against the same four leak categories
  but not mechanically swept. Extend the reflective guard, or add a sibling for the host project, to
  cover them.
- **Body-less 404 references, if a support scenario ever needs one.** The deliberate boundary above
  (bare 404s carry no correlation id) holds as long as a body-less 404 stays undiagnosable-by-design.
  If a real support case turns up needing to correlate a specific 404 request rather than "the id
  doesn't resolve," revisit converting the relevant bare 404s to the `ProblemDetails` shape.
- **`LateFeeRunStrategy.cs:101` / `RentRunStrategy.cs:67` GUID cleanup — resolved pre-merge.** Found
  during this WP: two of the three operator-facing exception strings in
  `LateFeeRunStrategy.PreviewAsync` were cleaned of the raw `row.LeaseId` GUID during this WP (the
  "multiple active leases" and "within the grace period" skip reasons now read `{row.TenantName}: …`
  only). The third, at line 101 (the "no late-fee policy found" skip reason), still interpolated the
  raw lease id, and final review found the same pattern at `RentRunStrategy.cs:67` (the "rent is 0"
  skip reason). Both were fixed pre-merge, on this branch, to the same `{row.TenantName}: …` shape
  as their siblings. Same leak category `DomainExceptionMessageTests` guards against
  in Accounting; both files live in `LeaseBook.Modules.Operations`, outside that guard's reflective
  scope — closing these two instances does not close the leak-guard-coverage gap itself (tracked
  above).

## Addendum (2026-07-31): WP-11 claims the 1200-1299 block

The second revisit trigger above has fired as designed — WP-11's nightly sweep landed and needed its
own log-event ids — and it asked for an addendum rather than a re-derived taxonomy, so this records
the outcome instead of adding a standalone ADR.

- **The block.** Scheduled jobs take **1200-1299**, the next hundred-block under the 1100+
  convention: `InvariantViolation` (1200, `Error`, one per org × invariant) and
  `InvariantSweepCompleted` (1201, `Information`, the clean-run heartbeat whose absence is the
  signal that the job did not run). Both are tabulated in
  [`docs/runbooks/diagnostics.md`](../runbooks/diagnostics.md) alongside the HTTP-surface ids.
- **Correlation, in the absence of a request.** The obligation above anticipated that a job has no
  `HttpContext` and therefore no correlation id to stamp. The resolution is that the sweep starts its
  own `jobs.invariant_sweep` activity and attaches each violation to it as a span event, so the run's
  trace is the correlation handle. This does not extend the ADR's HTTP-surface contract — a job still
  has no `Reference` for an operator to quote — it gives the background surface the equivalent
  engineer-side handle.
- **A violation is also durable outside the log pipeline.** The job throws on any violation, which
  records the run as Failed in Hangfire storage. That is deliberate redundancy: the one failure mode
  this job exists to catch must stay visible even if telemetry export is the thing that is broken.
