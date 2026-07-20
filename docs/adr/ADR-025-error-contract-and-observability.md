# ADR-025: Error contract, correlation ids, and diagnostic logging

- **Status:** Accepted
- **Date:** 2026-07-19
- **Deciders:** Engineering

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
(`tests/LeaseBook.Tests.Architecture/ErrorContractTests.cs`), which scans every `.cs` file under
`src/` for the regex `\b(?:TypedResults|Results)\.(?:Problem|ValidationProblem)\s*\(` — excluding
`ProblemResults.cs` itself and `obj`/`bin` — and fails with the offending file:line on any match. A
doc-comment convention was tried first and did not hold: the 28 pre-existing direct call sites (the
same figure cited in Context) are the proof, echoing the lesson ADR-012 and ADR-024 already drew for
the generated API client and the changelog — a convention without a gate rots.
Two scan limitations are accepted deliberately, not overlooked: the check is a raw-text match, so
(1) it can flag a **comment** that happens to contain the pattern (e.g. `// see Results.Problem(...)`)
— a false positive costs a one-line edit, and a text scan cannot distinguish code from comment without
becoming a much larger analyzer for one rule; and (2) it matches **per source line**, so a call whose
`TypedResults.Problem(` opening and arguments were deliberately split across multiple lines would not
match. Neither is a real threat in a solo-maintained repository — the practical failure mode the test
exists to catch is a new call site written in the natural single-line style, which it catches
reliably.
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
  call appears anywhere under `src/`, at the cost of the two named, accepted scan limitations.
- Frontend error handling collapsed from five independently drifted, hand-rolled mappers to one
  (`web/src/lib/apiError.ts` + `web/src/components/ApiErrorNotice.tsx`), fixing `reports.ts`'s
  silently-dropped validation branch as a side effect of consolidation rather than a separately scoped
  fix.
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
- The `ErrorContractTests` regex produces a false positive or false negative that costs real
  debugging time (a legitimate comment tripping the gate, or a deliberately multi-line call slipping
  through) — reconsider a Roslyn-based check at that point.
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
