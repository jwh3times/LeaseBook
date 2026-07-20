# LeaseBook Roadmap

- **Audience:** Evaluators, contributors, and maintainers
- **Status:** Living public direction
- **Owner:** Maintainers
- **Last reviewed:** 2026-07-20

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

Hardening and beta readiness are in progress. Shipped work includes CI end-to-end coverage, automated
WCAG 2 AA checks, visual-regression coverage for money-critical states, production safeguards for
development seed data, CSV formula-injection protection, authored Azure infrastructure, a security
hardening pass (security headers and CSP, enforced admin MFA, authentication rate limiting,
encrypted MFA secrets at rest, and production startup configuration guards), and a diagnostic
observability seam (a uniform API error contract with machine-readable codes and support-reference
correlation ids, safe user-facing error messages, and application logging wired to Application
Insights — see the error diagnostics runbook and ADR-025).

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
