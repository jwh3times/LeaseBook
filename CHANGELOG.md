# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). LeaseBook is pre-release and
has not yet cut a tagged version; everything implemented so far lives under **Unreleased** and will
be rolled into the first release when one is tagged.

## [Unreleased]

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

### Changed

- _Nothing yet._

### Fixed

- _Nothing yet._

<!-- Section vocabulary (Keep a Changelog): Added, Changed, Deprecated, Removed, Fixed, Security.
     When you tag a release, move the relevant Unreleased entries into a dated version section, e.g.:

## [0.1.0] - YYYY-MM-DD
### Added
- Initial public release.

     and add a matching link reference at the bottom. -->

[Unreleased]: https://github.com/jwh3times/LeaseBook/commits/main
