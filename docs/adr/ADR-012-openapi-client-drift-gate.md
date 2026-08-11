# ADR-012: Enforce the generated API client with a build-time OpenAPI drift gate

- **Status:** Accepted (amended by ADR-030)
- **Date:** 2026-06-15
- **Deciders:** Engineering

[ADR-030](ADR-030-hey-api-and-typescript-7.md) replaces this record's generator, generated-file
layout, and compiler pin. The build-time OpenAPI emission and drift-gate decision remain in force.

## Context

P11/WP-08 generate the SPA's typed client (`web/src/api/schema.d.ts`) from the host's OpenAPI
document, and the README states the consequence plainly: _"the frontend and backend contracts
cannot silently drift."_ But the only thing enforcing that was a human remembering to run
`npm run api:generate` (which needs the host running on `:5080`) and a reviewer catching a stale
file — a CONTRIBUTING checkbox, not a gate. A changed endpoint shipped with a stale `schema.d.ts`
would compile and pass CI. The guarantee was convention, not enforcement.

Two facts shaped the fix:

- **The doc can be produced without running the app.** `Microsoft.AspNetCore.OpenApi`'s companion
  build tool (`Microsoft.Extensions.ApiDescription.Server`) emits the document during `dotnet build`.
  In testing it produced a document **byte-identical in content** to the live `:5080` document —
  only the _order_ of paths differed (build-time enumeration vs. live `EndpointDataSource` order).
- **The generator runs the app's startup up to `app.Run()`.** Its `GetDocument` tool executes
  `Program.Main`. At the time of this decision, that reached the throwing startup role-seeding entry
  point (`RoleSeeder.EnsureRolesAsync`) — a database call — before `app.Run()`, so it failed with no
  database. Requiring a fully-migrated Postgres just to emit static API metadata would be
  disproportionate.

Separately, the TypeScript 6 major (Dependabot PR #9) is **blocked** because `openapi-typescript`
(latest 7.13.0) peer-caps at `typescript: "^5.x"`; no published release admits TS 6, and forcing it
would mean an unsafe `--legacy-peer-deps` resolve. The drift gate makes `openapi-typescript` a
CI-critical tool, which sharpens the need to track when that cap lifts.

## Decision

**A dedicated CI job regenerates the typed client from a build-time OpenAPI document and fails if it
differs from the committed copy.** Concretely:

- **Build-time emission.** `LeaseBook.Web` references `Microsoft.Extensions.ApiDescription.Server`
  (build-only assets). Generation is **off by default** (`OpenApiGenerateDocumentsOnBuild=false`) so
  the inner loop, the backend build, and the container build stay fast and DB-free; only the drift
  job opts in with `-p:OpenApiGenerateDocumentsOnBuild=true`. The document lands under `obj/`
  (gitignored), never the project root.
- **Startup guard.** Every pre-`Run()` database call is skipped when `LEASEBOOK_OPENAPI_BUILD=1` —
  originally just role seeding (now `RoleSeeder.TryEnsureRolesAsync`), since joined by the capability
  registry validation and the capability hosted services added in
  [ADR-028](ADR-028-platform-capability-model.md). That flag is set **only** by the drift job; it is
  unset in every real run (dev, prod, integration tests), so their behavior is unchanged. This keeps
  generation fully DB-free.
- **Canonical ordering.** Both `api:generate` and the gate pass `--alphabetize` to
  `openapi-typescript`, which sorts paths/types deterministically. This removes endpoint-ordering as a
  source of false drift (build-time order ≠ live order) and makes the committed file source-order
  independent. The committed `schema.d.ts` is stored in this canonical order.
- **The gate** (`.github/workflows/ci.yml` → `schema-drift` job) builds the host to emit the doc, runs
  `openapi-typescript … --alphabetize` over it, and `git diff --exit-code`s the result against the
  committed `schema.d.ts`, failing with a "run `npm run api:generate`" message on any difference.
- **Generated-file hygiene.** `schema.d.ts` is excluded from Prettier and the active linter (ESLint at
  the time of this decision; `Oxlint` under [ADR-029](ADR-029-frontend-linting-with-oxlint.md)), so
  `npm run format` cannot rewrite it and reintroduce drift.

**The held TS 6 upgrade is muted, not forgotten.** `.github/dependabot.yml` ignores `typescript`
`version-update:semver-major`, and `.github/workflows/ts6-unblock-watch.yml` checks weekly whether
the published `openapi-typescript` peer admits TS 6, opening a tracking issue when it does. (That
watcher was retired together with `openapi-typescript` under ADR-030. The equivalent un-mute signal
for the Hey API generator is `.github/workflows/codegen-unblock-watch.yml`, which _executes_ the
generator rather than reading a declared peer range — see ADR-030's revisit trigger.)

## Consequences

- **The README's promise is now true.** A contract change that lands without a regenerated client
  fails CI on the exact file to fix.
- **Generation stays cheap and DB-free.** No running host, no Postgres, no Kestrel — one `dotnet build`
  emits the doc; the drift job is the only place the tool runs.
- **Costs accepted.** Production startup carries a one-line, build-tooling-aware guard (documented at
  its call site); the drift job duplicates a backend build and `npm ci` (acceptable, runs in parallel);
  and `openapi-typescript` is now CI-critical, so the toolchain is pinned to TypeScript 5.x until that
  dependency supports 6 (see the watcher above).

## Revisit trigger

Reopen if **build-time generation stops being viable** — e.g., startup grows more pre-`Run()`
side effects than a single guard can reasonably cover, or a future `Microsoft.AspNetCore.OpenApi`
changes the build tool's behavior — in which case fall back to booting the host against a throwaway
Postgres (the `migration-check` pattern) and reading `/openapi/v1.json`. Independently, when the
`openapi-typescript` peer admits TypeScript 6, drop the Dependabot ignore and retire the watcher —
that second trigger is superseded by ADR-030, which replaced the generator and now owns the
compiler-unblock condition.
