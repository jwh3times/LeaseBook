# LeaseBook Roadmap

- **Audience:** Evaluators, contributors, and maintainers
- **Status:** Living public direction
- **Owner:** Maintainers
- **Last reviewed:** 2026-08-01

LeaseBook is pre-release software. This page communicates shipped capabilities and broad product
direction; it is not an implementation plan or a commitment to specific dates. Detailed sequencing,
security findings, compliance workpapers, and customer-specific planning are maintained privately.

## Current State

Milestones M0-M7 are complete:

- Foundations: authentication, authorization, tenant isolation, audit, CI, and the design system.
- Trust accounting: the double-entry journal, posting templates, dual-basis reads, and invariant,
  property-based, and golden-file verification.
- Directory and workflow: owners, properties, units, tenants, leases, dashboard, and search.
- Tenant ledger: payment and charge entry, deposits, prepayments, reversals, audit detail, and CSV
  export.
- Banking and reconciliation: projected bank registers, statement import, matching, reconciliation,
  period locking, and immutable reports.
- Reporting and operations: owner statements, fiduciary tie-outs, PDF/CSV reports, and bulk rent,
  late-fee, and owner-disbursement runs.
- Migration and onboarding: staged CSV import, opening-balance posting, verification, sign-off, and
  import-first onboarding.

Hardening and beta readiness are in progress. Shipped so far:

- **Quality gates.** End-to-end coverage in CI, automated WCAG 2 AA accessibility checks,
  visual-regression coverage for money-critical states, and a boot check that starts the released
  container stack rather than only building the image.
- **Security.** A hardening pass (security headers and a content-security policy, enforced admin
  MFA, authentication rate limiting, encrypted MFA secrets at rest, and production startup
  configuration guards), production safeguards for development seed data, and CSV
  formula-injection protection.
- **Diagnostics.** A uniform API error contract with machine-readable codes and support-reference
  correlation ids, safe user-facing error messages, and application logging wired to Application
  Insights — see the error diagnostics runbook and ADR-025.
- **Continuous fiduciary verification.** The trust-accounting invariants now run automatically each
  night across every organization, sharing a single code path with the on-demand check so the two
  cannot diverge, and record any violation under a stable event identifier. Scheduling is disabled
  by default and enabled in production; routing those events to an operator as an alert depends on
  a deployed environment and is not yet exercised.
- **Audit support.** A one-click trust compliance pack that bundles the period trust-equation
  tie-out, the trust-account ledger, the deposit-liability register, finalized reconciliation
  snapshots, and a money-touching audit extract for a fully reconciled period.
- **Migration close-outs.** An imported opening balance can be corrected before sign-off through an
  audited reversal-and-revision path, and un-swept management fees held in a trust account import
  as a first-class opening position that must be attested before go-live.
- **Measurement and fixtures.** A documented read-path latency budget measured against a
  design-scale synthetic organization, and an all-scenario fixture organization that exercises every
  posting template, workflow, and report under golden-file lock.
- **Authored Azure infrastructure.** Environment templates and deployment workflows exist; enabling
  them requires operator-held cloud access.

## Near-Term Priorities

Before beta, the project is focused on:

- Completing security, accessibility, performance, and operational hardening.
- Validating trust-accounting behavior and migration workflows against real operating scenarios.
- Completing compliance review and documented data-handling procedures.
- Exercising deployment, backup, restore, telemetry, and alerting procedures in a live environment.
- Closing remaining workflow gaps found during beta-readiness testing.

Work is considered ready only when the accounting invariants, tenant-isolation guarantees, documented
interaction budgets, and relevant automated gates remain green.

## Later Direction

After Phase 1 and beta readiness, planned product areas include online payments, owner and tenant
portals, fuller lease management, maintenance workflows, and vacancy/listing workflows. Detailed scope
and ordering will be defined at each phase boundary rather than inferred from this summary.

## Sources of Truth

- [Architecture](architecture.md) describes the system as implemented.
- [Accounting](accounting.md) describes the trust-accounting model and shipped workflows.
- [Architecture Decision Records](adr/README.md) record durable engineering decisions.
- [Changelog](../CHANGELOG.md) records released capabilities.

The code and accepted ADRs supersede this roadmap when implementation details differ.
