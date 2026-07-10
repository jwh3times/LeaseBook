# ADR-012: Enforce the generated API client with a build-time OpenAPI drift gate

- **Status:** Accepted
- **Date:** 2026-06-15
- **Deciders:** Engineering

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
  `Program.Main`, which reaches the startup role-seeding (`RoleSeeder.EnsureRolesAsync`) — a database
  call — before `app.Run()`. With no database it fails. Requiring a fully-migrated Postgres just to
  emit static API metadata would be disproportionate.

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
- **Startup guard.** The one pre-`Run()` database call (`RoleSeeder.EnsureRolesAsync`) is skipped when
  `LEASEBOOK_OPENAPI_BUILD=1`. That flag is set **only** by the drift job; it is unset in every real
  run (dev, prod, integration tests), so their behavior is unchanged. This keeps generation fully
  DB-free.
- **Canonical ordering.** Both `api:generate` and the gate pass `--alphabetize` to
  `openapi-typescript`, which sorts paths/types deterministically. This removes endpoint-ordering as a
  source of false drift (build-time order ≠ live order) and makes the committed file source-order
  independent. The committed `schema.d.ts` is stored in this canonical order.
- **The gate** (`.github/workflows/ci.yml` → `schema-drift` job) builds the host to emit the doc, runs
  `openapi-typescript … --alphabetize` over it, and `git diff --exit-code`s the result against the
  committed `schema.d.ts`, failing with a "run `npm run api:generate`" message on any difference.
- **Generated-file hygiene.** `schema.d.ts` is excluded from Prettier and ESLint (the prior
  `src/api/**/*.gen.ts` patterns never matched the real filename), so `npm run format` cannot rewrite
  it and reintroduce drift.

**The held TS 6 upgrade is muted, not forgotten.** `.github/dependabot.yml` ignores `typescript`
`version-update:semver-major`, and `.github/workflows/ts6-unblock-watch.yml` checks weekly whether
the published `openapi-typescript` peer admits TS 6, opening a tracking issue when it does.

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
`openapi-typescript` peer admits TypeScript 6, drop the Dependabot ignore and retire the watcher.
