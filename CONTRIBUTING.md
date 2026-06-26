# Contributing to LeaseBook

Thanks for your interest in improving LeaseBook. This is trust-accounting software, so the bar for
correctness is high — but the contribution process itself is ordinary. This guide covers how to get set
up, the conventions the codebase holds to, and what a change needs before it can merge.

By participating you agree to abide by our [Code of Conduct](CODE_OF_CONDUCT.md) — keep interactions
respectful and constructive.

---

## Getting set up

You'll need the **.NET 10 SDK**, **Node.js 26**, and **Docker** (for local PostgreSQL and the
Testcontainers-based test suites). The fastest way to a running app and the split inner-loop setup are
both documented in the [README](README.md#getting-started) and
[`docs/runbooks/local-dev.md`](docs/runbooks/local-dev.md).

Quick check that your environment is good:

```bash
dotnet build LeaseBook.slnx -c Debug     # 0 warnings, 0 errors
dotnet test LeaseBook.slnx                # Docker must be running
cd web && npm install && npm run typecheck && npm run test
```

---

## Workflow

1. **Open an issue first** for anything beyond a small fix, so the approach can be discussed before you
   invest time. For non-trivial design decisions, expect to record an ADR (see below).
2. **Branch** off `main` (e.g. `feature/owner-statements`, `fix/ledger-running-balance`). Keep branches
   focused — one logical change per pull request.
3. **Commit** in logical, self-contained steps with clear messages: a concise imperative subject line and
   a body explaining the *why*, not just the *what*.
4. **Open a pull request** against `main`. Fill in what changed and how you verified it. CI must be green
   and the PR reviewed before merge.

---

## Code conventions

The build enforces most of these — treat a clean `dotnet format`, ESLint, and the analyzers as the
minimum bar, not the goal.

### Backend (C# / .NET)

- **Nullable reference types and warnings-as-errors are on.** Run
  `dotnet format --verify-no-changes --exclude src/LeaseBook.Web/Migrations` before pushing.
- **Modular monolith boundaries are absolute.** A feature module references only `SharedKernel`. A
  cross-module read goes through a **consumer-owned port** (an interface in the consuming module's
  `Contracts`) implemented by a thin **host adapter** — never a direct reference to another module's
  types or a cross-module SQL/LINQ query. The architecture tests enforce the assembly half of this rule.
- **CQRS with vertical slices.** Commands and queries are records dispatched through the hand-rolled
  `ISender`; each has a colocated FluentValidation validator (the single validation home). **No MediatR,
  no AutoMapper.**
- **Endpoints are minimal APIs only** — bind → dispatch → `TypedResults`. No MVC controllers.
- **Money is `decimal` (C#) / `NUMERIC(14,2)` (Postgres). Never `float`/`double`.**
- **Every new org-scoped table** goes through the migrations row-level-security helper (column +
  `USING`/`WITH CHECK` policy + `FORCE ROW LEVEL SECURITY`); a schema-guard test fails CI otherwise.

### Frontend (React / TypeScript)

- Strict TypeScript; ESLint and Prettier are CI gates (`npm run lint`, `npm run typecheck`).
- Reusable UI primitives live in the design system (`web/src/design`); app-level shared components
  composed above them (page scaffolds, modals, the record quick-switch) live in `web/src/components`;
  `web/src/lib` holds pure TypeScript utilities and hooks only. Money renders through the `<Money>`
  primitive with tabular numerals and the organization's negative-display preference — never hand-formatted.
- Status is never conveyed by color alone (pair an icon or label with the color).
- The API client (`web/src/api/schema.d.ts`) is **generated** from the host's OpenAPI document
  (`npm run api:generate`) — don't hand-edit it; regenerate and commit it when the contract changes.
  CI's `schema-drift` job enforces this (ADR-012): it regenerates the client from a build-time copy
  of the contract and fails if the committed file is stale. It's excluded from Prettier/ESLint, so
  leave it exactly as the generator emits it.

### Architecture decisions

Significant or non-obvious decisions are captured as short **ADRs** in
[`docs/adr/`](docs/adr) (copy `docs/adr/template.md`). If your change deviates from an established
default, include the ADR in the same pull request.

---

## The accounting invariants (non-negotiable)

Correct trust accounting is the product, so changes that touch money are held to a higher standard. The
following are **correctness bugs, not style issues**, if violated:

- Management (PM) income is isolated from owner income at the data-model level — never reachable by an
  owner-statement query.
- Deposits and prepayments are liabilities until applied; income recognition fires only on application,
  identically in cash and accrual modes.
- Ledgers are append-only — corrections are linked reversal entries; posted rows are never updated or
  deleted.
- Every journal entry balances per basis, and the trust equation
  (`bank book balance = Σ owner equity + Σ deposit liabilities + held PM fees`) holds for every trust
  account at all times.

If your change is accounting-adjacent, it **must** keep the invariant, property-based, and golden-file
suites green, and add coverage at the right altitude. Don't casually edit the demo seed data — its
figures are golden-file fixtures; if they must change, change the corresponding tests deliberately.

---

## Testing expectations

Test at the right altitude and keep the suites green:

- **Unit / domain logic** — for posting templates, validators, and pure functions.
- **Invariant + property-based + golden-file** — touch these whenever the change is accounting-adjacent.
- **Integration** (Testcontainers + real migrations) — for the HTTP surface; tests run as the
  RLS-subject application role, so they exercise tenancy rather than bypassing it. Cross-org isolation is
  part of the permanent fixture.
- **End-to-end** (Playwright) — when a budgeted user flow changes.

Docker must be running for the integration and accounting suites.

---

## Pull request checklist

Before requesting review, confirm:

- [ ] `dotnet build LeaseBook.slnx -c Debug` is clean (0/0) and `dotnet test LeaseBook.slnx` is green.
- [ ] `dotnet format --verify-no-changes --exclude src/LeaseBook.Web/Migrations` reports no changes.
- [ ] Web `npm run lint`, `npm run typecheck`, `npm run test`, and `npm run build` are green (if the SPA changed).
- [ ] Accounting-adjacent changes keep the invariant/property/golden suites green.
- [ ] New org-scoped tables have their RLS policy; the schema guard passes.
- [ ] A regenerated, committed `schema.d.ts` accompanies any API-contract change (CI's `schema-drift` job enforces it).
- [ ] An ADR accompanies any significant design decision.
- [ ] No secrets, credentials, or confidential planning material are committed (the secrets scan runs in CI).

CI runs the full backend test suite against real PostgreSQL, type-checks and builds the web app, builds
the container image, and scans for secrets on every push and pull request.

---

## Reporting security issues

Please do **not** open a public issue for security vulnerabilities. See [SECURITY.md](SECURITY.md) for
how to report them privately.

---

## License

By contributing, you agree that your contributions are licensed under the project's
[GNU AGPL v3.0](LICENSE).
