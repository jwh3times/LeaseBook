# Product Scope

- **Audience:** Evaluators, contributors, and maintainers
- **Status:** Living public scope
- **Owner:** Maintainers
- **Last reviewed:** 2026-07-09

LeaseBook is property-management software for small residential property managers. Its defining
requirement is correct trust accounting with low interaction cost for recurring operational work.
This document states the public product boundary without pricing, customer-specific commitments, or
private delivery sequencing.

## Current Product Boundary

Phase 1 supports property-management staff operating a single organization with:

- Owners, residential properties, units, tenants, and lightweight lease records.
- A double-entry, dual-basis trust-accounting journal with append-only corrections.
- Tenant charges, payments, credits, deposits, prepayments, and applications.
- Bank registers, statement import, matching, reconciliation, and accounting-period locks.
- Owner statements, operational reports, PDF/CSV output, and delivery records.
- Preview-confirm-post bulk runs for rent, late fees, and owner disbursements.
- Staged migration imports, balance-forward opening entries, verification, sign-off, and onboarding.
- Role-based staff access, PostgreSQL row-level-security tenancy, audit events, and MFA capability.

The [accounting guide](accounting.md) owns financial behavior, and the
[architecture guide](architecture.md) owns system boundaries. This scope document does not restate
their detailed rules.

## Product Constraints

- Residential property management is the supported domain.
- The product is a web application; native mobile clients are not part of the current scope.
- USD is the current currency, while money remains represented by exact decimal types.
- Trust-accounting correctness and tenant isolation take precedence over convenience features.
- Budgeted workflows, accessibility, auditability, and demoability are release requirements, as
  defined in [CONTRIBUTING.md](../CONTRIBUTING.md).

## Explicit Non-Goals

The following are outside the planned product boundary through the currently defined phases:

- HOA, commercial, and short-term-rental management.
- Full general-ledger bookkeeping beyond the property-management trust-accounting model.
- Native mobile applications.
- Proprietary tenant-screening or listing-distribution engines.
- AI-generated operational or accounting decisions.
- A public third-party API.

Proposals in these areas require an explicit public scope change before implementation. Integration
seams may exist without making the external product itself part of LeaseBook.

## Later Product Areas

After Phase 1 and beta readiness, broad planned areas include online payments, owner and tenant
portals, fuller lease management, maintenance workflows, and vacancy/listing workflows. The
[roadmap](ROADMAP.md) communicates direction only; detailed scope is defined at each phase boundary.

## Public Contribution Boundary

Contributors can work from this scope, the public roadmap, accepted ADRs, and repository issues.
Private plans are not required to evaluate fixes, improve existing behavior, or implement an accepted
public issue. Any task whose acceptance criteria depend on unpublished commercial, customer, or
compliance decisions must be clarified by a maintainer before implementation.
