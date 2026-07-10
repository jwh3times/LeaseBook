# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). LeaseBook is pre-release;
released builds are tagged automatically from `VERSION` on merges to `main` in
`v<major>.<minor>.<build>` format, with the third component auto-incremented per major/minor line.
For a major or minor bump, `x.y.0` is a valid first release when that line has no existing tag.

**Cut policy:** `[Unreleased]` is cut into a dated version section at each **deliberate
major/minor bump** (the `VERSION` file changing its line); the per-merge build tags
(`vX.Y.<build>`) do not get their own sections and roll up into the next cut.

## [Unreleased]

### Added

- **Published engineering docs** — the architecture blueprint (`docs/blueprint.md`: tech defaults,
  the multi-tenancy/RLS design, and the trust-accounting data model) and the Definition of Done
  (`CONTRIBUTING.md`) are now committed, so a public clone has the durable engineering references
  needed to understand and contribute to the codebase.

### Changed

- **Documentation containment** — the public roadmap now presents shipped capabilities and broad
  direction without internal execution details; milestone retrospectives, planning-session
  artifacts, and unvalidated migration research remain private.

### Fixed

- _Nothing yet._

### Security

- _Nothing yet._

## [0.2.0] - 2026-07-09

### Added

- **Foundations** — email/password authentication with TOTP multi-factor, role-based authorization,
  PostgreSQL row-level security as the tenancy boundary (three database roles; per-transaction
  `SET LOCAL app.org_id`), an append-only audit log, the design system ported from the prototype,
  and a CI pipeline (build, tests against real PostgreSQL, web type-check/build, container build,
  secret scan, format/lint gates).
- **Trust-accounting engine** — a double-entry journal written only through dual-basis
  (cash/accrual) posting templates keyed to business events, a single write path, linked
  void/reversal, accounting periods, and a continuously-tested invariant suite (per-basis balance,
  the trust equation, deposit-liability non-negativity, management-income isolation).
- **Directory** — owners, properties, units, tenants, and lite leases, with list and detail screens,
  trigram-backed full-text search, a ⌘K command palette, and a dashboard showing all-owner ending
  balances at zero clicks.
- **Tenant ledger action hub** — record a payment or charge in place (≤ 3 interactions),
  collect/hold/apply deposits and prepayments, void with a linked reversal and a per-entry audit
  drawer, and a filterable, CSV-exportable running-balance ledger.
- **Banking & reconciliation** — a bank register and clearance layer projected from the immutable
  journal, reconcile-in-place to $0 with finalize, a per-account period lock, and an immutable
  reconciliation report, plus CSV statement import with auto-match and de-duplication (and the
  composite `(org_id, id)` journal-dimension foreign keys from the ADR-008 revisit).
- **Owner statements & reporting** — per-owner statements (per property or consolidated, in the
  org's selected basis) with a structural statement-to-ledger tie-out that blocks issuance on any
  non-zero variance, a computed fiduciary-integrity panel (PM income excluded, deposits recognized
  on application, variance $0.00), and a report catalog with real filters and live preview — owner
  statement, all-owner ending balances, trust-account ledger, bank reconciliation, deposit-liability
  register, rent roll, delinquency aging, and (PM-facing) management-fee income — each rendered to
  print-grade PDF (QuestPDF) or CSV, with a delivery seam that stores the immutable sent artifact.
  The statement engine reads across module schemas as the one sanctioned reporting read layer
  (ADR-016).
- **Bulk operations** — preview-confirm-post runs for the batch-shaped work: a monthly rent charge
  run (idempotent per month, proration at term boundaries), a late-fee run (grace/flat/percent
  policy with the NC §42-46 statutory clamp), and an owner disbursement run (per-owner net payable,
  held-back reserve, negative-balance exclusions, folding in the management-fee assessment) — each
  reviewable before posting and recorded as an auditable run with its preview snapshot
  (ADR-017/018/019).
- **Migration toolkit & import-first onboarding** — a tolerant CSV parse/validate library with
  per-entity AppFolio profiles, balance-forward opening-balance posting against a Migration Clearing
  account that nets to $0.00 per basis exactly when the import ties, a hard verification sign-off
  gate (go-live blocked until imported totals reconcile to the AppFolio closing figures), and a
  guided import-first onboarding wizard — a clean, verified cutover with no fabricated history
  (ADR-020/021).
- **Product hardening (M8, first tranche)** — the Playwright e2e suite now runs in CI with an automated
  WCAG 2 AA accessibility gate across every routed page (ADR-022) — now scanning both the light and
  dark themes, with the dark-theme contrast fixes (muted text, on-accent and accent-emphasis
  foregrounds, and two background-only buttons) it surfaced — visual-regression baselines on
  money-critical states via Playwright's built-in screenshot comparator with CI-rendered Linux
  baselines (ADR-023), and the authored Azure infrastructure (Bicep modules plus dev/prod deploy
  workflows, pending operator enablement).

### Security

- **Seeder production guard** — the demo and cutover seeders refuse to run outside a non-Production
  environment (fail-closed: an unset environment is treated as Production), so the source-committed
  admin credential can never provision a reachable org.
- **CSV formula-injection guard** — free-text cells in the ledger, owner-statement, and
  report-catalog CSV exports are neutralized (leading `= + - @`, tab, and carriage-return payloads),
  so an imported AppFolio value cannot execute as a formula when staff open the export in Excel.

<!-- Section vocabulary (Keep a Changelog): Added, Changed, Deprecated, Removed, Fixed, Security.
     When you prepare a release-line changelog entry, move the relevant Unreleased entries into a
     dated version section, e.g.:

## [0.1.0] - YYYY-MM-DD
### Added
- Initial public release.

     and add a matching link reference at the bottom. -->

[Unreleased]: https://github.com/jwh3times/LeaseBook/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/jwh3times/LeaseBook/releases/tag/v0.2.0
