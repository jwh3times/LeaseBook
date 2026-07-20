# Runbook: Diagnosing an error from its correlation reference

- **Audience:** Operators and maintainers
- **Status:** Living runbook; canonical error-diagnosis reference
- **Owner:** Maintainers
- **Last reviewed:** 2026-07-19

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

| Id   | Name                  | Level   | Meaning                                                                                       |
| ---- | --------------------- | ------- | --------------------------------------------------------------------------------------------- |
| 1000 | `UnhandledException`  | Error   | The terminal handler caught an exception no typed handler claimed. Always has the exception.  |
| 1001 | `DomainRejection`     | Warning | A typed accounting domain rule declined the request (a 404/409/422) — expected, not a defect. |
| 1002 | `ValidationRejection` | Warning | A command/query or auth DTO failed FluentValidation — a 400.                                  |
| 1003 | `ImportRowFailed`     | Error   | One row of a migration import failed after parsing; the batch continued. Has the exception.   |

1000-1099 is reserved for host/error plumbing; 1100+ is reserved for future domain areas as they
add their own structured events.

## Production caution: Npgsql `Include Error Detail`

Keep `Include Error Detail=true` **out of** production and staging Npgsql connection strings.
Configuration for those environments should not set it. This is a standing configuration
requirement, not a per-incident step — verify it once per environment, not per diagnosis.
