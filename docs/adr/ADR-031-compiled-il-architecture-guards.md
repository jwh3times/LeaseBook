# ADR-031: Inspect compiled IL for architecture guards

- **Status:** Accepted
- **Date:** 2026-08-10
- **Deciders:** Jerry Holland

## Context

Several architecture tests enforced runtime boundaries by matching C# source text. Those guards were
sensitive to comments, formatting, and file moves, and could pass vacuously when their source search
stopped finding the intended subject. Reflection and NetArchTest expose type-level dependencies but
not method calls or string-backed lookups such as raw SQL and reflective type names.

## Decision

Architecture guards that need method, field, type, or string references inspect compiled method bodies
with Mono.Cecil. The architecture-test project references Mono.Cecil directly and centralizes compiled
assembly discovery, reference extraction, capability-seam matching, and vacuity checks. Source scanning
remains for repository artifacts and cross-language rules that compiled assemblies cannot represent.

## Consequences

The guards follow async and compiler-generated methods without depending on source paths or spelling,
and shared positive controls make a broken inspector fail visibly. Mono.Cecil becomes a pinned test-only
dependency, and IL diagnostics identify compiled callers rather than source lines. New assemblies and
new forbidden-reference shapes must be added to the shared catalogs instead of individual tests.

### 2026-09-11 note — the class of rule compiled IL cannot express at any fidelity

Recorded here because an author asking "how do I guard this?" starts at this ADR and would otherwise
read an unqualified endorsement. IL inspection is unchanged and remains the right mechanism for what
it was chosen for; this names one class it cannot reach, which is a matter of **scope** rather than
the fidelity limit in the trigger below.

A guard that reads method references can only see calls that were made. A rule of the form "every
error response carries a `code` and a `correlationId`" is not a rule about calls at all — a response
written by middleware, by a framework event, or by hand onto `HttpResponse` satisfies or violates it
without referencing any factory, so no scan fidelity makes it visible. Two responses were missing the
contract for months on exactly that basis while `ErrorContractTests` stayed green (#357, #361).

The remedy is not a better scan. Where the property is about **what the system emits**, gate it
behaviorally against a running host: `MiddlewareErrorContractTests` does that for the error contract
(ADR-025, 2026-09-11 amendment 2). The two are complementary, and the trade is explicit — a call-site
scan is exhaustive over a compiled surface but answers only "was the factory used?", while a
behavioral gate answers "is the response right?" and is exhaustive over nothing but its own list of
cases. Prefer the behavioral gate whenever the invariant is stated in terms of an observable output.

## Revisit trigger

Revisit when Mono.Cecil cannot read the assemblies produced by the supported .NET toolchain, or when a
guard needs source-level semantics that cannot be recovered reliably from compiled method bodies.
