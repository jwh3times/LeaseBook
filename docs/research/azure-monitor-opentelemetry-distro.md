# Azure Monitor OpenTelemetry distro evaluation

- **Audience:** Maintainers and operators
- **Status:** Research note — recommendation for issue #163
- **Owner:** Maintainers
- **Last reviewed:** 2026-08-12

## Question

Should LeaseBook replace its manually assembled OpenTelemetry pipeline with the
`Azure.Monitor.OpenTelemetry.AspNetCore` distribution and `UseAzureMonitor()` for the current
operator-gated Application Insights work, or keep the distribution deferred until OpenTelemetry
meter instruments or Live Metrics are explicitly required?

## Current LeaseBook baseline

[The composition root](../../src/LeaseBook.Web/Program.cs) currently builds tracing and logging
separately. Tracing registers ASP.NET Core instrumentation and the custom `LeaseBook`
`ActivitySource`; logging includes formatted messages, parsed state, and scopes. Both signals add the
standalone Azure Monitor exporter only when `APPLICATIONINSIGHTS_CONNECTION_STRING` is non-empty.
The OpenTelemetry logger provider alone raises `Microsoft.EntityFrameworkCore` to `Warning`, so the
console provider keeps its normal development verbosity. There is no OpenTelemetry `MeterProvider`.

[ADR-025](../adr/ADR-025-error-contract-and-observability.md) chose that explicit composition while
only traces and logs were required. Its deferral condition—OpenTelemetry meter instruments or Live
Metrics entering explicit scope—remains unmet. The current operator-gated work requires Application
Insights queries, a workbook, and alerts over the existing click-budget activities and structured
logs, not a new metric signal or live stream.

The existing privacy regression test in
[`DeliverTelemetryTests`](../../tests/LeaseBook.Tests.Integration/Security/DeliverTelemetryTests.cs)
also establishes a behavior that must not regress: an inbound request containing a recipient email
address in its query string must not put that value on the ASP.NET Core activity.

## What the distribution changes

| Concern            | Current manual composition                           | `UseAzureMonitor()`                                                                                                                                                                    | LeaseBook consequence                                                                                                                                                     |
| ------------------ | ---------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Traces             | ASP.NET Core plus the `LeaseBook` source             | ASP.NET Core, `HttpClient`, Azure SDK, and SQL client instrumentation are registered by the distribution.                                                                              | Keep the custom source and service resource explicitly; review the additional dependency/SQL telemetry.                                                                   |
| Logs               | OpenTelemetry logger options are explicit.           | The distribution enables formatted messages and parsed state, but scopes remain opt-in.                                                                                                | Retain an explicit `OpenTelemetryLoggerOptions.IncludeScopes = true` configuration.                                                                                       |
| Metrics            | No OpenTelemetry `MeterProvider` is created.         | On supported .NET versions the distribution collects ASP.NET Core hosting and `System.Net.Http` meters and enables Azure Monitor standard metrics and performance counters by default. | This capability is outside the current operator-gated scope; when it is required, the enabled instruments and dimensions need an explicit cost/cardinality review.        |
| Live Metrics       | Not available through the standalone exporter setup. | Enabled by default in the ASP.NET Core distribution.                                                                                                                                   | This capability is also outside the current scope. If it becomes required, make the choice explicit in options and validate both telemetry and control channels in Azure. |
| Export reliability | Uses the standalone Azure Monitor exporter.          | Uses the same exporter underneath.                                                                                                                                                     | Retry and offline persistence are not unique benefits of the distribution.                                                                                                |

The trace, metric, resource-detector, log, standard-metric, and Live Metrics registrations above are
visible in the distribution's
[`UseAzureMonitor` implementation](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/monitor/Azure.Monitor.OpenTelemetry.AspNetCore/src/OpenTelemetryBuilderExtensions.cs).
Microsoft's distribution README also documents the default instrumentation, custom-source extension
point, metric controls, opt-in scopes, and Live Metrics behavior.
([Azure Monitor OpenTelemetry ASP.NET Core README](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/Monitor.OpenTelemetry.AspNetCore-readme?view=azure-dotnet))

### Metrics and Live Metrics

The distribution enables standard metrics, performance counters, and Live Metrics by default;
`EnableStandardMetrics`, `EnablePerformanceCounters`, and `EnableLiveMetrics` are all explicit
options. Its current default trace rate limit is five traces per second, while metrics are not
sampled.
([`AzureMonitorOptions` source](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/monitor/Azure.Monitor.OpenTelemetry.AspNetCore/src/AzureMonitorOptions.cs),
[sampling guidance](https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-configuration))

Live Metrics is an on-demand, approximately one-second stream with no retention; it is intended for
interactive production diagnosis rather than durable reporting. Microsoft describes it as free and
notes that the feature has a bidirectional control channel, which is why authentication of that
channel matters when filters are used.
([Live Metrics documentation](https://learn.microsoft.com/en-us/azure/azure-monitor/app/live-stream))

### Retries and offline storage

Both choices ultimately use `Azure.Monitor.OpenTelemetry.Exporter`. The exporter persists failed
telemetry locally and retries it for up to 48 hours; offline storage can be disabled or moved with
`DisableOfflineStorage` and `StorageDirectory`. Default locations are temporary/local application
data directories, including `/tmp`, `/var/tmp`, or `$TMPDIR` on Linux.
([Azure Monitor OpenTelemetry configuration](https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-configuration#offline-storage-and-automatic-retries))

The exporter intentionally sets Azure Core's immediate retry count to zero and supplies its own
persistent transmission path. This means distro adoption preserves the existing exporter family’s
reliability mechanism rather than adding a second retry layer.
([`AzureMonitorTransmitter` source](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/monitor/Azure.Monitor.OpenTelemetry.Exporter/src/Internals/AzureMonitorTransmitter.cs))

Container Apps ephemeral storage can therefore buffer a transient outage but should not be treated
as a 48-hour durability guarantee across restarts. LeaseBook should either accept that operational
limit, configure a suitable writable directory, or explicitly disable offline storage; this is an
operational decision, not a reason by itself to choose the distribution.

### Connection-string-absent behavior

LeaseBook currently starts normally without an Application Insights connection string and simply
does not add either exporter. `UseAzureMonitor()` must not be called unconditionally: the underlying
transmitter looks first at configured options and then at
`APPLICATIONINSIGHTS_CONNECTION_STRING`, and throws `InvalidOperationException` when neither
contains a connection string.
([`AzureMonitorTransmitter.InitializeConnectionVars`](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/monitor/Azure.Monitor.OpenTelemetry.Exporter/src/Internals/AzureMonitorTransmitter.cs))

Preserving the existing contract therefore requires a composition branch: use the distribution only
when a non-empty connection string is present, and retain a local no-export OpenTelemetry pipeline
otherwise. This also keeps telemetry absent from intentionally uninstrumented short-lived job/CLI
hosts unless they are separately designed and tested for flushing.

### Entra authentication

The distribution accepts a `TokenCredential` through `AzureMonitorOptions.Credential`; the
standalone path exposes the same capability through `AzureMonitorExporterOptions.Credential`. For a
user-assigned managed identity, Microsoft recommends `ManagedIdentityCredential` with that identity's
client ID. The connection string remains necessary to identify the Application Insights resource and
ingestion endpoint; the credential replaces the authentication secret carried by local
authentication.
([Entra authentication for Application Insights](https://learn.microsoft.com/en-us/azure/azure-monitor/app/azure-ad-authentication),
[`AzureMonitorOptions` source](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/monitor/Azure.Monitor.OpenTelemetry.AspNetCore/src/AzureMonitorOptions.cs))

The identity needs the **Monitoring Metrics Publisher** role scoped to the Application Insights
resource; despite its name, Microsoft documents that role for publishing all telemetry. Local
authentication (`DisableLocalAuth`) should be disabled only after the managed identity, role
assignment, SDK credential, ordinary ingestion, and Live Metrics have been deployed and verified
together.
([Entra authentication prerequisites](https://learn.microsoft.com/en-us/azure/azure-monitor/app/azure-ad-authentication#prerequisites))

LeaseBook already attaches a user-assigned identity to its Container App, so Entra is a compatible
next hardening step. It is not automatic distro behavior: the application must reference
`Azure.Identity`, supply the correct client ID to `ManagedIdentityCredential`, and pass that
credential to Azure Monitor options while infrastructure grants the role.

### Provider-scoped EF logging filter

The existing filter can remain unchanged. `AddFilter<TProvider>` applies a rule only to the named
`ILoggerProvider`, and the distribution still exports logs through
`OpenTelemetryLoggerProvider`; it does not require broadening the EF threshold across console or
other providers.
([`AddFilter<TProvider>` API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.filterloggingbuilderextensions.addfilter?view=net-10.0-pp),
[`UseAzureMonitor` logging registration](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/monitor/Azure.Monitor.OpenTelemetry.AspNetCore/src/OpenTelemetryBuilderExtensions.cs))

Keep this as a provider-scoped rule and add a focused registration test if the composition is
refactored. A category-wide minimum level would change local diagnostics and is not equivalent.

### Query-string redaction

This is the largest adoption hazard. Standalone ASP.NET Core and `HttpClient` instrumentation redact
query values by default. The Azure Monitor distribution deliberately sets both
`OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION` and
`OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION` to `true` when they are absent,
which disables that protection.
([ASP.NET Core instrumentation redaction behavior](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.AspNetCore/README.md),
[`UseAzureMonitor` redaction defaults](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/monitor/Azure.Monitor.OpenTelemetry.AspNetCore/src/OpenTelemetryBuilderExtensions.cs))

Before registering the distribution, LeaseBook must explicitly set both configuration keys to
`false`. The existing inbound-email regression test must remain, and an outbound `HttpClient` test
should cover a sensitive query value as well. Omitting either override would regress LeaseBook's
current privacy boundary.

### Custom activities and correlation

The `LeaseBook` activity source remains supported, but it is not discovered automatically. Register
it through `ConfigureOpenTelemetryTracerProvider(... AddSource(LeaseBookTelemetry.SourceName))` and
retain the `LeaseBook.Web` service resource. Microsoft documents this hook specifically for custom
`ActivitySource` instances used with `UseAzureMonitor()`.
([custom telemetry guidance](https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-add-modify?tabs=net))

Application Insights correlation follows W3C activity context: the activity trace ID becomes the
operation ID and parent/child activity IDs link the operation. Therefore the existing error
contract's correlation token remains compatible as long as the custom source and ambient
`Activity.Current` flow are preserved.
([Application Insights .NET SDK correlation clarification](https://github.com/microsoft/ApplicationInsights-dotnet/issues/2579))

Sampling is the subtle part of that contract. The distribution currently defaults to a five-trace-
per-second rate limit, and its trace-based log sampler drops logs attached to unsampled traces by
default. Metrics are never sampled.
([sampling configuration](https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-configuration),
[`AzureMonitorOptions` defaults](https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/monitor/Azure.Monitor.OpenTelemetry.AspNetCore/src/AzureMonitorOptions.cs))

For beta, LeaseBook should configure 100% trace sampling explicitly. If trace sampling is introduced
later, set `EnableTraceBasedLogsSampler = false` so error logs remain searchable even when their
request span is not retained, and document that a user-facing correlation token may then resolve to
logs without a complete trace.

## Recommendation

**Defer the ASP.NET Core distribution again and keep the manual pipeline.** The current
operator-gated work can build Application Insights queries, a workbook, and alerts over LeaseBook's
existing activities and structured logs. It does not require the distribution's OpenTelemetry
metrics or Live Metrics, so adopting it now would add broader instrumentation and migration risk
without satisfying a present requirement.

Re-open this decision when OpenTelemetry meter instruments, Live Metrics, Azure resource detection,
or another distribution-only capability becomes an explicit acceptance criterion. At that point,
use this constrained-adoption blueprint for the long-running web host:

1. Branch on a non-empty Application Insights connection string. Use `UseAzureMonitor()` only in the
   configured branch and retain a no-export OpenTelemetry pipeline otherwise.
2. Set both ASP.NET Core and `HttpClient` disable-redaction keys to `false` before distribution
   registration; keep the inbound regression and add the outbound one.
3. Re-register `LeaseBookTelemetry.SourceName`, the `LeaseBook.Web` resource, scopes, and parsed log
   state explicitly.
4. Keep the typed `OpenTelemetryLoggerProvider` EF filter.
5. Configure trace sampling explicitly at 100% for beta; revisit sampling and trace-based log
   sampling as one correlation-policy decision.
6. Set the metric, performance-counter, and Live Metrics choices explicitly rather than inheriting
   package-version defaults. Validate actual exported instruments and dimension cardinality.
7. Treat offline storage as best-effort on Container Apps ephemeral disk and record the chosen
   storage policy in the diagnostics runbook.
8. Add Entra authentication as an atomic application-and-infrastructure hardening change: grant the
   user-assigned identity **Monitoring Metrics Publisher**, pass `ManagedIdentityCredential`, verify
   ingestion and Live Metrics, and only then disable local authentication.
9. Do not automatically extend the distribution to one-shot migration, seed, capability, or other
   CLI/job paths; give any such host its own connection, privacy, and flush design.

Future adoption must be a constrained migration, not a silent package swap. Without the
absent-connection branch and the two redaction overrides, the distribution would make startup less
resilient and telemetry less private than the current implementation.

## Verification checklist

- Start the web host with no connection string and confirm health/readiness plus local logging still
  work without an Azure exporter.
- Start with a connection string and assert one exporter per signal—no duplicate ASP.NET activities
  or duplicate log records.
- Retain the inbound recipient-email redaction regression and add an outbound `HttpClient` sensitive
  query regression.
- Assert custom `LeaseBook` activities and request activities share the expected W3C trace ID; verify
  the user-facing correlation token resolves in Application Insights.
- Confirm EF `Information` logs remain visible locally but are excluded from the OpenTelemetry
  provider, while EF warnings still export.
- In a deployed environment, verify request/dependency traces, exception logs, ASP.NET Core and HTTP
  metrics, standard metrics, Live Metrics, and telemetry recovery after a brief ingestion outage.
- For Entra, verify ordinary ingestion and the Live Metrics control channel before setting
  `DisableLocalAuth` on the Application Insights resource.
