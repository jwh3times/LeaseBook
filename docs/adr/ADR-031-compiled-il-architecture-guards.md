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

## Revisit trigger

Revisit when Mono.Cecil cannot read the assemblies produced by the supported .NET toolchain, or when a
guard needs source-level semantics that cannot be recovered reliably from compiled method bodies.
