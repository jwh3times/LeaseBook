# ADR-042: Make host process lifecycle explicit

- **Status:** Accepted
- **Date:** 2026-09-02
- **Deciders:** Engineering

## Context

LeaseBook ships one executable that serves three purposes: the Web host, foreground operator CLI
verbs, and build-time OpenAPI document generation. They share application composition but have
different allowed startup effects.

Those differences accumulated as conditionals in `Program.cs`. The OpenAPI-build flag was interpreted
at nine executable decision sites; CLI selection controlled early error rendering, logging and a late
return; and `Jobs:Enabled` was interpreted separately for Hangfire registration and recurring-job
scheduling. The order of those blocks—not an interface—was the process-mode contract.

This fired ADR-012's revisit trigger: build-time generation remained viable and desirable, but
startup had grown beyond a single database guard. The code still had no live startup defect, so the
change must preserve the established behavior rather than redesign readiness, security, scheduling,
or CLI semantics while moving it.

## Decision

**One internal concrete host-lifecycle module selects, configures, and runs exactly one process
mode.** It is not an injected port or adapter: there is one in-process implementation, so an
interface would be a hypothetical seam.

The modes are mutually exclusive:

| Concern                                               | Web                 | CLI | OpenAPI build |
| ----------------------------------------------------- | ------------------- | --- | ------------- |
| Shared application service graph                      | yes                 | yes | yes           |
| HTTP pipeline and endpoint surface                    | yes                 | no  | yes           |
| Durable Postgres/Key Vault Data Protection            | yes                 | yes | no            |
| Forwarded-header trust and production web warnings    | yes                 | no  | no            |
| Capability and role-seeding background workers        | yes                 | no  | no            |
| Hangfire and recurring scheduling                     | when `Jobs:Enabled` | no  | no            |
| Foreground CLI invocation                             | no                  | yes | no            |
| Role seeding, security guards and registry validation | yes                 | no  | no            |

`Jobs:Enabled` and the ASP.NET environment remain inputs to Web policy, not additional process modes.
A recognized CLI verb combined with `LEASEBOOK_OPENAPI_BUILD=1` is rejected before host composition;
there is no hybrid-mode precedence rule.

The module exposes three lifecycle operations:

1. Resolve argv and the OpenAPI-build signal into one mode or an early terminal error.
2. Configure mode-sensitive host infrastructure before the provider is built.
3. Run the selected lifecycle after shared composition, accepting the mode-neutral HTTP-pipeline
   definition and invoking it only for Web or OpenAPI generation.

`Program.cs` continues to own module registration, callable shared services, and the HTTP-pipeline
definition. It does not interpret process-mode booleans. CLI retains the shared service graph but
runs immediately after provider construction, before web middleware, endpoint mapping, production
warnings, role seeding, registry validation, or scheduling. `CliApplication` remains the sole owner
of verb grammar and execution; the lifecycle does not introduce per-verb capability profiles.

Build-time OpenAPI generation remains database-free. The real schema-drift job continues to execute
the application and capture its endpoint document, but its lifecycle does not activate the durable
keyring, deployment proxy configuration, workers, database startup work, validation, or jobs.

Tests exercise selection, registration and activation through the lifecycle surface. Existing
full-host readiness and job tests continue to own Web behavior, and the schema-drift build remains
the executable proof that OpenAPI generation needs no database.

## Consequences

- A new startup concern has one place to declare which process may activate it. Adding a fourth mode
  requires extending one exhaustive lifecycle rather than finding scattered exclusions.
- CLI commands no longer construct an unused HTTP pipeline or register Web-only workers and Hangfire.
  They still resolve the shared application graph, avoiding a second registration graph that could
  drift from the product host.
- ADR-012's OpenAPI gate stays fast and database-free, but its old mechanism—individual
  `LEASEBOOK_OPENAPI_BUILD` guards in the composition root—is superseded by this lifecycle.
- ADR-001's Hangfire ownership and failure policy, ADR-028's capability/readiness behavior, ADR-041's
  durable keyring and proxy-trust rules, and ADR-025's Web logging contract remain unchanged.
- The lifecycle necessarily knows several host infrastructure types. That concentration is the
  intended depth: deleting it would redistribute the process matrix and ordering rules into
  `Program.cs` again.

## Revisit trigger

Reopen if LeaseBook gains a second executable with a genuinely different composition root, a CLI
verb can no longer use the shared graph without activating unrelated external dependencies, or the
OpenAPI build tool can no longer capture the endpoint surface through the selected lifecycle.
