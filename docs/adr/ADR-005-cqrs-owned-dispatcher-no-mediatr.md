# ADR-005: CQRS via an owned dispatcher + FluentValidation; no MediatR, no AutoMapper

- **Status:** Accepted
- **Date:** 2026-06-12
- **Deciders:** Engineering

## Context

The application pattern is CQRS with vertical slices per module. The common libraries for this —
MediatR (dispatcher/pipeline) and AutoMapper (DTO mapping) — became commercially licensed from
v13 and v15 respectively. Adopting them out of habit creates a license obligation for a product
whose differentiation is correctness, not request-routing plumbing. The dispatcher we need is
small and the mapping is better done explicitly for money-touching code.

## Decision

- **Dispatcher:** a hand-rolled `ISender` (~50 lines) in `SharedKernel` resolves the handler for a
  message's runtime type from the DI scope and invokes it. Handlers are `ICommandHandler<,>` /
  `IQueryHandler<,>`, registered by assembly scan (Scrutor).
- **Pipeline:** decorators composed in pinned order — telemetry (outermost) → validation → handler
  (P24) — applied via Scrutor `Decorate` over the open generic handler interfaces.
- **Validation:** **FluentValidation** (Apache-2.0), one validator per command/query colocated with
  its slice, executed by the `ValidationDecorator` as the _single_ validation execution point.
  Endpoint filters never re-validate.
- **Mapping:** DTOs are hand-mapped `record`s. No AutoMapper.
- **Endpoints:** minimal-API `IEndpointModule` per module (no MVC controllers).

## Consequences

- No third-party runtime license obligation for the core request path.
- We own ~50 lines of dispatcher and the decorator wiring (tested in
  `LeaseBook.Tests.SharedKernel`); the small reflection cost per dispatch is negligible at our
  scale.
- Hand-mapping is more verbose than AutoMapper but explicit — appropriate where a wrong field on a
  money DTO is a correctness bug.

## Revisit trigger

If the dispatcher/pipeline grows materially in complexity (e.g., streaming, notifications,
sagas) such that maintaining it costs more than a license, re-evaluate a licensed/library option.
