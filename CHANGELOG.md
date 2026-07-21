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
- **Documentation governance** — a public documentation index, classification and ownership policy,
  and sanitized product-scope contract now make the repository self-contained for contributors.
- **Documentation quality gate** — CI now formats, structurally lints, spell-checks, and validates
  public docs, including lifecycle metadata, private-link boundaries, command ownership, and ADR index
  consistency.
- **Changelog enforcement** — CI now fails a pull request that changes product source without adding
  to the `[Unreleased]` changelog section, so every shipped change is recorded before it merges;
  Dependabot bumps and a `skip-changelog` label are exempt.
- **Dark-theme visual regression** — the CI visual gate now covers the dark theme for the three
  theme-sensitive states (dashboard, ledger composer, owner statement) alongside the existing light
  baselines, so a dark design-token regression fails CI instead of shipping unnoticed.
- **Extended end-to-end coverage** — the CI e2e suite now exercises Directory-navigation depth
  (owner/property/tenant records, ⌘K jumps, and the record quick-switcher), the designed error and
  empty states (server-failure and no-data rendering), and keyboard-only operability (command
  palette, reconcile selection, focus return, autofocus, and focus-visible rings), closing the last
  of ADR-022's deferred coverage.
- **Trust Compliance Pack** — a one-click, PMAdmin-only audit bundle
  (`GET /api/reports/compliance-pack`) for a trust account × period, downloaded as a ZIP of discrete
  documents: a cover/index PDF carrying the period-end trust-equation tie-out, the trust-account
  ledger, the security-deposit liability register, the finalized reconciliation report snapshots, and
  a money-touching audit-log extract, plus a manifest. It composes existing reads (computes no new
  figures); no PM-income figure appears in any owner-facing artifact; generating a pack is itself
  audited; and a pack is produced only when every month in the period is reconciliation-locked (so the
  bundle can't change after it is handed to an auditor). Supported by period-end
  read parameters on the trust-equation and deposit-register reads (variance proven 0.00 at any
  as-of). See the ADR-016 addendum.
- **Full-stack boot gate** — the container CI job now brings up the Compose `full` profile
  (db → migrate → seed → app), waits for `/api/health`, and asserts the app served without ever
  restarting. It previously only proved the image compiled; nothing ever started it, so a startup
  misconfiguration could put the documented local stack into a silent restart loop and still pass
  every check. The restart-count assertion is the load-bearing one — a crash-looping container
  reports itself as `running` between bounces, so a liveness check alone does not catch it. Booting
  the stack subsumes the old build-only check and also builds the `migrator` image target, which no
  job built before.
- **Performance fixture and latency harness** — `seed --org load` provisions a ~300-unit synthetic
  org (25 owners, 40 properties, 12 months of activity, ~7,700 journal entries) generated entirely
  through the real posting engine and bulk-run engine, and a new `perf-probe` verb measures
  p50/p95/p99 on the four money-critical read paths — tenant ledger, dashboard, bank register, and
  owner statement — against a running host, exiting non-zero when p95 misses the 300 ms budget.
  The first measurement puts all four an order of magnitude inside budget, so no query and no index
  changed; notably the owner statement, assembled from the live journal, comes in at 21.7 ms p95, so
  ADR-016's revisit trigger for materializing statement read models has not fired at this scale.
  Method and numbers are recorded in `docs/perf.md`. Not a CI gate — runner variance would make a
  latency threshold flaky.
- **Data-handling and privacy compliance drafts** — a public `docs/compliance/` set now documents the
  GLBA data map, the encryption/access/retention posture, and a GLBA-style privacy-notice skeleton
  with explicit legal-review markers, giving the external compliance review a versioned engineering
  packet to finalize. Draft status; legal determinations are deferred to that review.
- **Correlation-id error surface** — every error response and error alert now carries a
  machine-readable `code` plus a correlation reference: a selectable `Reference: <32-hex>` string
  that is the same trace id Application Insights indexes as `operation_Id`, so an operator can quote
  it directly in a support conversation and an engineer can search on it with no separate lookup
  step. `ILogger` output now routes through OpenTelemetry to Application Insights (a local no-op
  until a connection string is configured), and a terminal exception handler ensures nothing falls
  through to a silent, uncoded 500. See ADR-025.
- **Diagnostics runbook** — `docs/runbooks/diagnostics.md` documents how to turn an on-screen
  correlation reference into an Application Insights query, and lists the stable `LogEvents` ids
  Track B's alert rules will key on.
- **Pre-sign-off import correction (supersede)** — a cutover operator can now correct an
  already-posted opening balance before sign-off instead of re-provisioning the whole org.
  `POST /api/onboarding/import-balances/{kind}/supersede` diffs a corrected file against the live
  opening positions per `source_ref` family: a changed figure posts a linked reversal (dated at the
  cutover boundary) plus a corrected revision (`#r{N}`); a position left out of the file is
  untouched (omission is not removal); a position resubmitted at $0.00 is removed outright. The
  successor batch records `supersedes_batch_id` for lineage, and after sign-off the endpoint returns
  409 — corrections become ordinary ledger reversals instead. See ADR-021.
- **Held-PM-fees opening import** — a fifth balance kind: an un-swept property-management-fee
  position sitting in a trust or deposit-purpose bank account now imports as a real opening position
  (a `pm_income` credit plus a `migration_clearing` contra, both bases, bank-dimensioned, no owner
  dimension — ADR-020 §5) instead of surfacing only as a migration-clearing residual. The migration
  verification report carries a dedicated **Held PM Fees (Cash)** line, sign-off is refused
  (`held_fees_not_attested`) until the operator attests to a non-zero position, and the PM-facing
  management-fee income report excludes opening-balance postings (and their voids) so an imported
  position never inflates in-period fee income.

### Changed

- **Documentation containment** — the public roadmap now presents shipped capabilities and broad
  direction without internal execution details; milestone retrospectives, planning-session
  artifacts, and unvalidated migration research remain private.
- **Canonical documentation ownership** — architecture, accounting, development commands, product
  scope, and the Definition of Done now have explicit public owners; `CLAUDE.md` is a tool adapter
  over the canonical cross-agent contract in `AGENTS.md`.

### Fixed

- **Modal focus management** — closing a dialog now returns focus to the control that opened it, and
  opening one moves focus to its first field (falling back to a button when the dialog has no field),
  fixing a keyboard focus-order gap (WCAG 2.4.3) surfaced by the new keyboard-only e2e.
- **Not-found entry void returns 404** — voiding a nonexistent or cross-org journal entry now returns
  a 404 (`entry_not_found`) instead of a generic 500, matching the read endpoints; the nonexistent and
  cross-org cases are indistinguishable, so there is no existence oracle.
- **Local full-stack run starts again** — the Compose `app` service did not set an environment, so it
  defaulted to Production and the new startup guards failed it on the empty `AllowedHosts`; with
  `restart: unless-stopped` that was a silent crash loop that left `./scripts/dev.ps1 app-up`
  unusable. The service is now explicitly `Development`, matching the `seed` service beside it.
- **Container binds its documented ingress port** — `appsettings.Development.json` pinned `Urls` to
  the inner-loop address `http://localhost:5080`, which overrode the image's `ASPNETCORE_HTTP_PORTS`
  and made the container listen on loopback:5080 instead of `:8080`, so the published port answered
  nothing. That key was redundant — the inner loop already gets `:5080` from `launchSettings.json`
  and the e2e host passes `--urls` explicitly — so it has been removed.
- **Reachable frontend error copy** — two error responses (`not_tied`, `statement_not_balanced`) set
  a human-readable `title` but never the machine-readable `code` extension every frontend error
  mapper reads, so the friendly copy already written for one of them could never render. Every error
  response now goes through one factory that stamps `code` consistently, enforced by a build-time
  source scan; the frontend's five independently drifted error mappers were consolidated into one.
- **Owner statements no longer fail for months containing voided entries or migration opening
  positions.** A void now nets inside the section of the entry it reverses, and opening-balance
  entries dated inside the statement month fold into the beginning balance; the statement tie-out
  remains an independent journal re-check.

### Security

- **Host security hardening** — a defense-in-depth pass on the backend: security response headers and
  a strict Content-Security-Policy on every response (including error responses), production
  host-header filtering, secure cookies outside Development, rate limiting on the authentication
  endpoints, config-gated multi-factor enforcement for admin accounts, encryption of sensitive
  authentication data at rest, an authorization-matrix regression guard, and a fail-fast startup check
  that blocks a non-Development boot when required security configuration is missing.
- **Safe error responses** — error responses and import row errors no longer include internal
  exception detail; unexpected errors return a generic response with a support reference.

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
