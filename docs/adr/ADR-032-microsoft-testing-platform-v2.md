# ADR-032: Execute xUnit tests with Microsoft Testing Platform v2

- **Status:** Accepted
- **Date:** 2026-08-11
- **Deciders:** Jerry Holland

## Context

LeaseBook targets .NET 10 and uses executable xUnit v3 test projects, but those projects still ran
through the VSTest compatibility path. xUnit now ships an explicit MTP v2 package, and .NET 10 can
select the MTP implementation of `dotnet test` repository-wide. Keeping both runners would preserve
an untested compatibility path and two different filter/reporting command surfaces.

## Decision

Select `Microsoft.Testing.Platform` in `global.json` and reference `xunit.v3.mtp-v2` from every
executable test project. Remove `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`. Contributor
commands use MTP's class and method filters, CI emits xUnit TRX reports through MTP, and supported
Visual Studio Test Explorer usage starts at Visual Studio 2022 17.14.

## Consequences

CLI, CI, and supported IDE execution share one runner and one option surface, and CI retains portable
test-result artifacts. Older Visual Studio and VSTest-only tools are no longer supported; contributors
must use the repository-pinned .NET 10 SDK and an MTP-capable IDE.

## Revisit trigger

Revisit if a required test or IDE workflow cannot run on MTP v2, or if a supported development
environment requires VSTest compatibility again.
