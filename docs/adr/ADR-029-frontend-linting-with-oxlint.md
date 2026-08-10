# ADR-029: Frontend linting with `Oxlint` and type-aware `tsgolint`

- **Status:** Accepted (amended by ADR-030)
- **Date:** 2026-08-10
- **Deciders:** Jerry Holland

[ADR-030](ADR-030-hey-api-and-typescript-7.md) removes the application-level TypeScript 5
constraint described here. The linting decision remains in force.

## Context

The SPA used ESLint, typescript-eslint, and separate React plugins. That stack duplicated parser and
plugin dependencies, and typescript-eslint's compiler peer range made the lint toolchain another
constraint on future TypeScript upgrades. LeaseBook also needs linting to catch promise and unsafe-type
errors that syntax-only rules cannot detect.

The OpenAPI generator still independently constrains the application to TypeScript 5.x under
[ADR-012](ADR-012-openapi-client-drift-gate.md); replacing the linter does not remove that separate
constraint.

## Decision

Use `Oxlint` as the SPA's only linter. Remove ESLint, typescript-eslint, their React plugins, globals,
and the ESLint configuration. Keep the stable `npm run lint` interface, backed by `oxlint .` and
`web/.oxlintrc.json`.

Enable `Oxlint`'s type-aware mode through `oxlint-tsgolint`. Type-aware rules include rejected or
floating promise checks and unsafe TypeScript operations. Keep the application, Node tooling, and
Playwright e2e projects in the referenced TypeScript project graph; `tsconfig.e2e.json` gives the e2e
suite its own environment and types. The generated OpenAPI declaration remains excluded from both
formatting and linting so generation stays deterministic.

## Consequences

- One native lint command replaces the ESLint parser/plugin stack while preserving the CI and
  contributor-facing command.
- Linting now performs compiler-backed analysis, so rules can find errors that syntax-only linting
  cannot. It also depends on valid TypeScript project references and costs more than an untyped `Oxlint`
  pass.
- The lint stack no longer carries typescript-eslint's compiler peer cap, but TypeScript 7 remains a
  separate migration because the OpenAPI client generator still caps its peer range.
- `Oxlint` does not provide exact one-for-one coverage for every ESLint ecosystem plugin. Required
  React Hooks and Fast Refresh checks stay enabled through `Oxlint`'s React plugin; any future missing
  rule must be evaluated explicitly rather than restoring a second linter implicitly.

## Revisit trigger

Revisit if a required correctness rule is unavailable in `Oxlint`, if type-aware linting cannot support
the repository's TypeScript compiler, or if `oxlint-tsgolint` is replaced by a different supported
type-aware integration.
