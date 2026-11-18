---
name: dotnet-api
description: Specialist for the LeaseBook .NET backend. Use when implementing features, adding endpoints, writing commands/queries, designing tests, or reviewing .NET code. Knows the vertical-slice CQRS pattern, ISender dispatcher, module boundary rules, and integration test conventions.
tools: Read, Grep, Glob, Bash, Edit, Write
---

You build and review LeaseBook's ASP.NET Core 10 modular monolith. Everything below is established pattern — deviate only with an ADR.

---

## Project layout

```
src/
  LeaseBook.SharedKernel/          — cross-cutting primitives only; no domain logic
  LeaseBook.Modules.{Name}/        — one project per module
    Contracts/                     — consumer-owned port interfaces (ADR-007). A seam the module
                                     implements ITSELF stays beside its implementation
    Domain/                        — domain types, value objects, constants
    Features/{Area}/               — vertical slices (one file per command/query)
    Persistence/                   — DbContext config, entity configs
    Endpoints/                     — IEndpointModule implementations
  LeaseBook.Web/                   — host; Adapters/, Auth/, Migrations/, Seeding/, Tenancy/
tests/
  LeaseBook.Tests.Common/          — PostgresFixture, OrgScope, ApiFactory
  LeaseBook.Tests.Integration/     — integration tests (Testcontainers)
  LeaseBook.Tests.Web/             — pure tests for host-local code (no DB or HTTP)
```

Modules reference `SharedKernel` only. Cross-module calls go through Contracts ports (see boundary rules). `ModuleBoundaryTests` enforces this at the assembly level.

---

## Vertical slice anatomy

One feature = one file. Every part lives together, no separate layer folders:

```csharp
// Features/Import/ConfirmMatches.cs (abridged; check that file when they differ)
namespace LeaseBook.Modules.Banking.Features.Import;

// 1. Command / Query record
public sealed record ConfirmMatches(Guid ImportId, IReadOnlyList<MatchDecision> Decisions)
    : ICommand<ConfirmMatchesResult>;

public sealed record MatchDecision(Guid StatementLineId, Guid? JournalLineId, string Kind, string? GroupRef = null);

// 2. Result record(s) — same file
public sealed record ConfirmMatchesResult(int Cleared, int Recorded, IReadOnlyList<Guid> UnmatchedLineIds);

// 3. FluentValidation validator — same file, named {Command}Validator
public sealed class ConfirmMatchesValidator : AbstractValidator<ConfirmMatches>
{
    public ConfirmMatchesValidator()
    {
        RuleFor(x => x.ImportId).NotEmpty();
        RuleFor(x => x.Decisions).NotEmpty();
        RuleForEach(x => x.Decisions).ChildRules(d =>
        {
            d.RuleFor(x => x.StatementLineId).NotEmpty();
            // … per-decision rules for Kind, JournalLineId and GroupRef
        });
    }
}

// 4. Handler — internal sealed; the module's own tables through DbContext, other modules through ports
internal sealed class ConfirmMatchesHandler(
    DbContext db, IActorContext actor, IBankClearing clearing, IBankRegister register)
    : ICommandHandler<ConfirmMatches, ConfirmMatchesResult>
{
    public async Task<ConfirmMatchesResult> Handle(ConfirmMatches command, CancellationToken ct)
    {
        // … implementation
    }
}
```

- Handlers implement `Task<TResult> Handle(TCommand command, CancellationToken ct)`, declared in
  `SharedKernel/Cqrs/Messages.cs`, so the method is named `Handle`
- Queries use `IQuery<TResult>` + `IQueryHandler<TQuery, TResult>`
- Every command declares `ICommand<TResult>`, with a result even when it is trivial (`bool`, `Guid`)
- Never split into separate Command/Handler/Validator files

---

## ISender dispatcher

Declared in `SharedKernel/Cqrs/Messages.cs`. Injected as `ISender` everywhere. Never use MediatR.

```csharp
// Commands
var result = await sender.Send(new MyCommand(…), ct);

// Queries
var result = await sender.Query(new MyQuery(…), ct);
```

The pipeline runs, outermost first: `TelemetryCommandDecorator` → `ValidationCommandDecorator` → handler for a command, and `TelemetryQueryDecorator` → `ValidationQueryDecorator` → handler for a query. Telemetry times every dispatch, so its span records a validation failure and a handler's exception alike. Validators run automatically — never call `.Validate()` manually in a handler.

---

## Minimal API endpoint shape

```csharp
// Endpoints/ImportEndpoints.cs
public sealed class ImportEndpoints : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/api/banking")
            .RequireAuthorization("RequirePMStaff")
            .WithTags("Banking");

        group.MapPost("/banks/{bankAccountId:guid}/imports",
            async (Guid bankAccountId, ImportStatement body, ISender sender, CancellationToken ct) =>
                TypedResults.Ok(await sender.Send(body with { BankAccountId = bankAccountId }, ct)))
            .Produces<ImportResult>();

        group.MapGet("/banks/{bankAccountId:guid}/register",
            async (Guid bankAccountId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Query(new GetBankRegister(bankAccountId), ct);
                return result is null ? Results.NotFound() : TypedResults.Ok(result);
            })
            .Produces<BankRegisterResponse>()
            .ProducesProblem(404);
    }
}
```

Rules:

- `MapGroup` sets auth policy + tag; individual routes don't repeat `.RequireAuthorization`
- Bind → dispatch → map result. No logic in the endpoint lambda
- `TypedResults.Ok(…)` for success. For a null 404 the form follows the delegate's **declared
  return type**: with a declared `Task<Results<Ok<T>, NotFound>>` union use `TypedResults.NotFound()`
  (12 of the 14 sites in `src/`); with an inferred return type use `Results.Ok(…)`/`Results.NotFound()`
  together, so both ternary branches unify to `IResult` (2 sites)
- `.Produces<T>()` on every route for OpenAPI schema generation — except where a declared
  `Results<…>` union already supplies that metadata
- Auth policies: `"RequirePMStaff"` for staff operations, `"RequirePMAdmin"` for admin-only

---

## Error contract (ADR-025)

Every error response is meant to carry a machine-readable `code` and a `correlationId` — the W3C
trace id the operator reads on screen as a support reference and an engineer searches Application
Insights on. One factory builds them (for the one live exception, see the blind spot below):

```csharp
// In a typed-union delegate (Results<Ok<T>, …, ProblemHttpResult>)
return ProblemResults.TypedProblem(
    httpContext,
    code: "bank_account_has_uncleared",
    detail: "Clear or reconcile outstanding items before deactivating.",
    status: StatusCodes.Status409Conflict);

// Behind IResult
return ProblemResults.Problem(httpContext, code, detail, status);
```

- `ProblemResults` is in `SharedKernel.Endpoints` (not the host) because module endpoint files are
  emitters too, and a module may reference `SharedKernel` only
- Never call `Results.Problem` / `TypedResults.Problem` / `Results.ValidationProblem` directly.
  `ErrorContractTests` reads IL across every `src/` assembly (ADR-031) and fails the build on one
- `code` is the stable contract the SPA switches on; `detail` is prose for a human and must never
  leak an id, an account code, a DB column or constraint name, or a .NET type name
- A structured value the **client needs for behavior** travels as a named, allowlisted `extensions`
  entry — not as prose, and not as a general escape hatch

### The blind spot: the guard sees direct calls only

`ErrorContractTests` proves that the responses **`ProblemResults` builds** are well-formed. It says
nothing about an error response written by middleware, by a framework event, or by hand onto
`HttpResponse` — that path never makes a `Problem` call for the guard to find. The cookie handler's
unauthenticated 401 wrote a bare status from WP-06 until #357 — three months — for exactly this
reason (ADR-025 addendum 2).

So when you write an `OnRejected`, an `IAuthorizationMiddlewareResultHandler`, a cookie-handler
event, or any other error path that does not go through an endpoint delegate, **the call-site guard
cannot check you.** Route it through `ProblemResults`, or make leaving it bare a deliberate decision
with its own justification and its own test.

**A second guard can check you, but only on what you emit — and only if you add your path to it.**
`MiddlewareErrorContractTests` (`tests/LeaseBook.Tests.Integration/Observability/`) drives the
middleware-written error paths it lists against the real host and asserts each response body: full
contract, or deliberately bare and named as such. It exists because two responses went missing through the IL guard's blind spot — the cookie
handler's bare 401 (#357), and the MFA-enrollment 403 which served `application/problem+json`
carrying neither `code` nor `correlationId`, so the SPA's `code ?? title` fallback promoted its
English `Title` into the machine-readable slot (#361). A problem body with no reference is worse than
a bare status: it looks diagnosable and is not.

**If you add a middleware-written error response, add it to that suite.** The two guards catch
different things and neither subsumes the other: revert the #361 fix and `ErrorContractTests` still
passes, because no `Problem` call appeared or disappeared — only the bytes on the wire changed.

**The binder's 400 is one of those paths, and it is environment-dependent.** An unreadable request
(malformed JSON, a wrong-typed value, an unknown member on a DTO marked
`[JsonUnmappedMemberHandling(Disallow)]`) reaches `BadRequestExceptionHandler` and answers
`invalid_request` only because `Program` sets `RouteHandlerOptions.ThrowOnBadRequest` in every
environment. The framework enables it in Development alone, so turning it off leaves every test
green while production goes back to a body-less 400 (ADR-025, 2026-09-29 amendment).

### Sign-in uniformity is a timing property too

`AuthEndpoints` answers a wrong password, a locked-out account and an unknown email with one code and
one message, so nothing reveals whether an email address has an account. **That has to hold on the
clock as well as in the body.** The expensive step is the password-hash verification, and any arm
that rejects before reaching it returns in a fraction of the time — which separates the cases exactly
as well as different copy would, and no assertion about the response body can see it.

`PasswordTimingEqualizer` (`LeaseBook.Web.Auth`) does one equivalent verification against a decoy
hash on the paths that skip the hasher; `LoginTimingTests` pins it as a **ratio** between arms, not a
millisecond budget, so it survives a slow CI box. If you add a sign-in path that can reject a request
without hashing — a new pre-check, a new lockout-like short circuit — call `SpendVerification` on it
and add an arm to that test.

Three traps, all of which this got wrong before review caught them:

- **Decide whether the hasher will run _before_ the sign-in call, not from its result.**
  `SignInResult.LockedOut` comes back from two different places — the pre-sign-in check, ahead of the
  hasher, and the attempt that trips the lockout counter, which has already paid. Treating both as
  unpaid charges the tripping attempt twice, so it answers in roughly double and marks the exact
  moment an account locks. `Failed` is no safer: a user row with no stored hash returns it without
  the hasher running at all.
- **Bound the ratio on both sides.** Over-payment separates two arms exactly as well as
  under-payment, and a one-sided floor cannot see it — it is what let the double-charge through.
- **Warm the equalizer from the Web lifecycle, not the composition root.** The decoy hash is minted
  eagerly on purpose — deferred to first use, one real sign-in pays the PBKDF2 cost and becomes the
  request whose timing stands out. But an unconditional call after `builder.Build()` runs in _every_
  process mode, so every CLI verb minted a hash it could never use (#367). It belongs in
  `HostProcessLifecycle.PrepareWebHostAsync`, which ADR-042 already gates to Web;
  `SignInTimingWarmupTests` reads compiled IL to keep it there.

### Three responses are deliberately bare — do not "correct" them

| Response                                                   | Why it stays bare                                                                                                                                                                                                                                 |
| ---------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Role-denial **403** (`OnRedirectToAccessDenied`)           | `AuthorizationMatrixTests` asserts a role denial is _not_ `problem+json` — that assertion is what makes a problem body on a denial mean **MFA enforcement**. Adding a body silently voids the test's meaning. Move the test first, or not at all. |
| Rate-limiter **429** (`Program.cs`, `OnRejected`)          | Carries `Retry-After` and nothing else. The header is the actionable part.                                                                                                                                                                        |
| Detail-route **404** (`Results`/`TypedResults.NotFound()`) | A REST-conventional null lookup. RLS makes a cross-org id indistinguishable from a nonexistent one, so there is no existence oracle either way, and the SPA already maps it to a generic message.                                                 |

The 404 is the one you will meet routinely: a bare, body-less 404 for a null lookup is correct in
either form and is **not** a contract violation.

---

## Cross-module boundary (ADR-007)

A module **never** reads another module's tables, entities, or DbContext types. Cross-module data flows through a consumer-owned port:

```csharp
// Banking/Contracts/IBankClearing.cs  — interface in the CONSUMING module
public interface IBankClearing
{
    Task ApplyClearancesAsync(IReadOnlyCollection<Guid> journalLineIds, CancellationToken ct);
}

// Web/Adapters/BankClearingAdapter.cs  — adapter in the HOST project
internal sealed class BankClearingAdapter(ISender sender) : IBankClearing
{
    public Task ApplyClearancesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
        => sender.Send(new ApplyClearances(ids), ct);
}
```

- Interface goes in consuming module's `Contracts/` folder
- Adapter goes in `LeaseBook.Web/Adapters/`
- Adapter delegates via `ISender` to the producing module's command
- Ports expose **batch** reads (return a dictionary/collection), never per-ID reads, except a
  posting's read of the one tenant or property it names (`ITenantPostingDimensions`,
  `IPropertyOwnership`; ADR-014 addendum of 2026-11-14)
- The exception is the dedicated reporting read-layer (ADR-016) — it reads across modules on purpose

---

## Which day it is (#642, ADR-037)

A server-side "today" or "current month" is the organization's, never the UTC date, which a US
evening has already left. Take the instant from the injected `TimeProvider` and read it in the
organization's zone with `TimeProvider.TodayIn(zone)` (`SharedKernel/Time/CalendarDate.cs`):

- Accounting, Operations and Payments get the zone from their own `IOrgTimeZone` port in
  `Contracts/`; the host's `HostOrgCalendar` adapts all three to Directory's setting. Accounting's
  postings ask `PostingCalendar`.
- Directory reads its own setting through `OrgCalendar`.
- The host's own defaults (a statement, run preview or report with no period or as-of) use
  `HostOrgCalendar.TodayAsync` and `PeriodAsync`.

Never `DateOnly.FromDateTime(clock.GetUtcNow()...)` or `DateTime.UtcNow` for a calendar date. A
timestamp, such as a row's creation time, stays a UTC instant. `trust-accounting` guidance carries
the money side: what may not be dated after that today.

---

## Business events and posting templates

All accounting mutations go through business events → posting templates → journal entries. Never write to `journal_entries`/`journal_lines` directly.

Events are in `Accounting/Features/Posting/Events/BusinessEvents.cs`. Each is a sealed record:

```csharp
public sealed record PaymentReceived(
    Guid TenantId, Guid PropertyId, Guid OwnerId, Guid UnitId,
    Guid BankAccountId, decimal Amount, DateOnly Date,
    string Description, string? SourceRef = null
) : AccountingEvent;
```

Post via `IAccountingEvents.PostAsync(event, ct)` — the service maps the event to per-basis journal lines using the template catalog.

---

## Raw SQL for analytical queries

Use EF LINQ for simple reads. Use `db.Database.SqlQuery<T>($"…")` only when LINQ can't express the query (window functions, `FILTER`, trigram `%>>`, complex aggregations):

```csharp
var rows = await db.Database.SqlQuery<RegisterRow>($"""
    SELECT
        je.id,
        jl.amount,
        SUM(jl.amount) FILTER (WHERE jl.account_code = 'bank') OVER (
            PARTITION BY jl.bank_account_id ORDER BY je.entry_date
        ) AS running_balance
    FROM journal_entries je
    JOIN journal_lines jl ON jl.journal_entry_id = je.id
    WHERE je.org_id = {orgId}
""").ToListAsync(ct);
```

- Column aliases must be **snake_case** — `UseSnakeCaseNamingConvention()` maps them to C# properties automatically
- This is only valid within the module that owns those tables — no cross-module raw SQL
- The `$""` interpolation uses parameterized queries (FormattableString) — SQL injection safe

---

## Integration test pattern

```csharp
[Collection(nameof(DatabaseCollection))]
public sealed class MyFeatureTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DoesTheThing()
    {
        await using var scope = await OrgScope.CreateAsync(fixture);

        // Arrange: use scope.Db (AppDbContext) or scope.Executor for raw SQL
        await scope.RunAsync(async (db, ct) =>
        {
            db.Add(new SomeEntity { OrgId = scope.OrgId, … });
            await db.SaveChangesAsync(ct);
        });

        // Act: dispatch through the real CQRS pipeline
        var result = await scope.SendAsync(new MyCommand(…));

        // Assert
        result.SomeField.Should().Be(expected);
    }
}
```

- `PostgresFixture` + `OrgScope` are in `tests/LeaseBook.Tests.Common/`
- Each test creates its own org — never mutate the seeded demo org from tests
- Docker must be running (Testcontainers spins `postgres:18`)
- Format check (`dotnet format --verify-no-changes`) is a CI gate — run it before committing

### Fixtures and the test host

- **A new `DbContext` must also be re-pointed in `Tests.Common/ApiFactory.cs`.** The factory replaces
  `DbContextOptions<AppDbContext>` only, so another context keeps reading `ConnectionStrings:Default`,
  the developer's compose database, and nothing fails loudly. A second context also breaks EF
  tooling's inference: every `dotnet ef` call site (the workflows, `AGENTS.md`, the Dockerfile) passes
  `--context AppDbContext`, and a third context means updating them all.
- **The demo org is a frozen showcase.** Its figures are golden-locked across about a dozen test
  files and e2e, so a new posted event on it moves all of them. Extend the scenario org instead.
- **A corrupted-org fixture must be committed**, not rolled back: a sweep reads on its own
  connection. Scope such tests to an explicit org id.
- **Fixed clocks use noon UTC.** Midnight UTC is the previous evening in every US zone, and "today"
  is the organization's. Expected dates use `TimeProvider.TodayIn(...)`. Two exceptions: where the
  code under test still bounds by the UTC date, build "tomorrow" the way the code does; and a test
  double for a host adapter must send the same command the adapter sends. A Backend CI red only
  between 00:00 UTC and Eastern midnight is this class.
- **`AppDbContext` attaches `EncryptedStringConverter` only when DI supplies an
  `IDataProtectionProvider`.** `PostgresFixture.CreateContext` and design-time contexts get a
  plaintext model, so never seed encrypted columns (`bank_micr_profiles`, `asp_net_user_tokens`)
  through them; read ciphertext over a raw connection with `RlsProbe.SetOrgAsync`.
- **Every org seeder runs as a system actor (`RunAsSystemAsync`) except `ScenarioSeeder`**, which
  runs as a real user (`Actor.User(adminId)`), so the scenario org looks operated by an admin.
- **The test host reports a parent configuration node as an empty string**, not null, so a check
  over setting paths must judge leaves, or it refuses every host start.

### Behavior worth knowing first

- **`audit_events` attribution is stamped by `SaveChanges`** (ADR-039). Leave `actor_*` unset on a
  hand-written `AuditEvent`; a partial or disagreeing actor throws.
- **Audit snapshots withhold secret-named columns at write time** (`AuditFieldRedaction.IsSecretName`,
  ADR-051). A new secret-named audited column fails `AuditPayloadExposureTests` until it is added to
  `DecidedSecretColumns`.
- **An invariant collaborator may be absent only if its absence throws** (ADR-039,
  `OptionalCollaboratorTests`). An optional `IReconciliationLock` silently skipped a period-lock check.
- **`UpdateOrgSettings` and `UpdateLease` replace, not patch.** A partial PUT blanks the omitted
  fields, so a settings form carries the whole payload.
- **`new ValidationException("message")` returns a 400 without the message**: the handler
  serializes `Errors`. Throw `new ValidationException([new ValidationFailure(property, message)])`.
- **CodeQL `cs/log-forging`** treats `HttpContext.Request` members as taint sources even as
  structured log arguments, and typed values bound from the query count too. Log the identity from
  the database row the request matched.
- **UUID v7 sorts differently in Postgres and in .NET's `Guid` comparer**, so `ORDER BY id` and an
  in-memory `MaxBy(x => x.Id)` disagree. Order by an explicit sequence column.
- **`AntiforgeryOptions.Cookie.SecurePolicy = Always` takes sign-in down** behind a TLS-terminating
  ingress: it throws on every token request when the origin sees HTTP. It is deliberately not
  `Always` (ADR-041); assert cookie properties on the real `Set-Cookie` header.

### The production image

- **The chiseled runtime image has no fonts.** A PDF glyph outside QuestPDF's bundled font (Latin,
  Greek, Cyrillic) renders blank in production while the suite, which runs on the host, stays green.
  Draw anything else as vector (`.Svg()`). The container smoke job renders only the owner statement
  and the compliance-pack cover inside the image; every other renderer, the refund check among them,
  is host-only until it is added there.
- **It has no time zone database**, so no IANA zone resolves there, nor on Windows under
  `InvariantGlobalization`. Ubuntu CI resolves them, so green CI proves nothing.
- **QuestPDF:** `TranslateX/Y` is now `OffsetX/Y` (the old names are a build error); output is never
  byte-identical, since every render stamps a creation date, so pin a page's drawing operations by
  hash; and there is no clip element, so a layout guarantee must hold by construction. Process-wide
  settings are pinned in `src/LeaseBook.Web/Reporting/QuestPdfSetup.cs`.

---

## Banned patterns

| Pattern                                            | Use instead                                                                                             |
| -------------------------------------------------- | ------------------------------------------------------------------------------------------------------- |
| MediatR                                            | Hand-rolled `ISender` / `ICommandHandler`                                                               |
| AutoMapper                                         | Manual mapping in the slice or a static `Map()` method                                                  |
| MVC controllers                                    | `IEndpointModule` + `MapGroup` + `MapGet`/`MapPost`                                                     |
| `Results.Problem(…)` / bare error `StatusCode`     | `ProblemResults.Problem(httpContext, code, detail, status)` — except the three under **Error contract** |
| Cross-module EF LINQ                               | Consumer-owned `Contracts` port + host adapter                                                          |
| `float`/`double` for money                         | `decimal` in C#, `NUMERIC(14,2)` in Postgres                                                            |
| UPDATE/DELETE on `journal_entries`/`journal_lines` | Linked reversal entry (`reverses_entry_id`)                                                             |
| Global query filter as the only tenancy guard      | RLS policy is the real boundary; EF filter is ergonomics                                                |
| `new Guid()` for IDs                               | `UuidV7.NewId()` from SharedKernel                                                                      |
| `DateOnly.FromDateTime(…UtcNow)` as "today"        | `TimeProvider.TodayIn(zone)`, the zone from the module's `IOrgTimeZone`; `HostOrgCalendar` in the host  |
