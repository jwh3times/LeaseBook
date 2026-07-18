# Data Handling and Safeguards

- **Audience:** Contributors, maintainers, and the external compliance reviewer
- **Status:** Draft — pending external GLBA/NCREC compliance review
- **Owner:** Maintainers
- **Last reviewed:** 2026-07-18

> **Draft status.** This document is engineering-authored and **not yet accepted**. What blocks
> acceptance is the external trust-accounting and privacy compliance review — the NCREC-facing review
> anticipated by [ADR-014](../adr/ADR-014-reconciliation-engine-and-lock.md)'s revisit trigger and
> supported by the trust compliance pack recorded in the
> [ADR-016 addendum](../adr/ADR-016-reporting-read-layer.md). This file states engineering facts and
> the safeguards design; sentences that assert a legal conclusion are deferred to that review and
> carry a `[LEGAL REVIEW]` marker in the companion [privacy-notice draft](privacy-notice-draft.md).
>
> **Public-safe.** Per the [documentation policy](../documentation-policy.md), this file carries no
> pricing, customer identity, confidential strategy, active security findings, or private
> infrastructure values. Detailed compliance workpapers remain private.
>
> **Deployment status.** LeaseBook is pre-beta. The application and its data model run today; the
> Azure infrastructure that carries several safeguards below is authored in `infra/` and enabled as
> the first environment is provisioned. Each safeguard is marked **Live** (running today) or
> **At go-live** (authored, enabled during environment provisioning).

## 1. Purpose and scope

LeaseBook is a property-management trust-accounting system for North Carolina residential brokers. It
holds nonpublic personal and financial information about property owners, tenants, and the managing
brokerage, and it keeps the fiduciary trust records a brokerage must maintain under NCREC Rule
58A .0116. This document is the data map and safeguards description that supports a GLBA Safeguards
review: what data the system holds, where it lives, how it is protected in transit and at rest, who
can reach it, how long it is kept, and how it leaves the system.

Whether LeaseBook is itself a "financial institution" under GLBA or a service provider to one (the
managing brokerage) is a legal determination reserved for the compliance review — see the
[privacy-notice draft](privacy-notice-draft.md). The safeguards below apply regardless of that
classification.

## 2. Data inventory (data map)

All application data lives in a single multi-tenant PostgreSQL database, partitioned per organization
by row-level security (§4). Every table below carries an `org_id` and is reachable only within its
organization's row-level-security context, except the identity and platform tables noted as
global-class.

### 2.1 Nonpublic personal information

| Data                                                | Where it lives                     | Notes                                               |
| --------------------------------------------------- | ---------------------------------- | --------------------------------------------------- |
| Owner name, email, phone                            | `owners` (Directory)               | Contact detail for statement recipients             |
| Tenant name, email, phone                           | `tenants` (Directory)              |                                                     |
| Property street address, city, state, ZIP           | `properties` (Directory)           |                                                     |
| Managing brokerage legal name, address, phone       | `org_settings` (Directory)         | Firm identity rendered on statements                |
| User email, phone, username, display name           | `asp_net_users` (host)             | Application login accounts                          |
| Recipient email of a delivered statement            | `statement_deliveries` (Reporting) | Delivery record for a sent statement                |
| Verbatim imported records (names, emails, balances) | `import_rows` (Onboarding)         | Raw and mapped migration data, kept as import audit |

### 2.2 Financial information

| Data                                                           | Where it lives                                                          |
| -------------------------------------------------------------- | ----------------------------------------------------------------------- |
| Chart of accounts, accounting periods                          | `accounts`, `accounting_periods` (Accounting)                           |
| Double-entry journal (headers, debit/credit lines, memos)      | `journal_entries`, `journal_lines` (Accounting)                         |
| Bank reconciliation state and finalized snapshots              | `bank_reconciliations`, `bank_line_status` (Accounting)                 |
| Imported bank statement lines and match decisions              | `statement_imports`, `statement_lines`, `statement_matches` (Banking)   |
| Bank account name, institution, masked number, purpose         | `bank_accounts` (Directory) — numbers stored **masked**, last four only |
| Rents, deposits, lease-level fee terms                         | `units`, `lease_lite` (Directory)                                       |
| Bulk run headers and line results (rent/late-fee/disbursement) | `bulk_runs`, `bulk_run_items` (Operations)                              |
| Migration opening balances and verification figures            | `import_batches`, `migration_verifications` (host)                      |

### 2.3 Credentials and authentication data

| Data                                        | Where it lives                                           | Notes                                                                |
| ------------------------------------------- | -------------------------------------------------------- | -------------------------------------------------------------------- |
| Password hashes, security and lockout state | `asp_net_users` (host)                                   | ASP.NET Identity; passwords are salted-hashed, never stored in clear |
| Multi-factor (TOTP) enrollment and tokens   | `asp_net_users`, `asp_net_user_tokens` (host)            | Authenticator state for two-factor sign-in                           |
| Role and claim assignments                  | `asp_net_roles`, `asp_net_user_roles`, `*_claims` (host) | Authorization data                                                   |

Credential tables are **global-class** (not organization-scoped) and are never copied into the audit
trail (§2.5).

### 2.4 Artifacts (generated documents)

Delivered owner-statement PDFs are written to an immutable, write-once artifact store
(`IArtifactStore`) so the stored copy is exactly what the owner received; these PDFs contain owner
name, property address, and financial figures. The store is a local filesystem today and moves to
Azure Blob Storage at go-live (two containers — `statements` and `documents`). Compliance packs,
report CSVs, and on-demand statement downloads are streamed to the requester and **not** persisted.

### 2.5 Audit trail

`audit_events` records a before/after snapshot of every money- or entity-touching change, with the
acting user, entity, action, and timestamp. Because it stores full row snapshots, it transitively
retains historical copies of the personal and financial fields above — this is deliberate: the audit
trail is the tamper-evident fiduciary record. Credential tables are excluded by construction. The
audit table is append-only (§4).

### 2.6 Telemetry

Interaction telemetry (`/api/telemetry/budget`) records only a task label, an interaction count, and
a pass/fail boolean for the product's click-budget goals. It carries **no** names, amounts, or entity
identifiers. When configured, spans are exported to Azure Application Insights.

### 2.7 Backups

Point-in-time recovery retains database backups (§5), which necessarily include copies of all
PostgreSQL data above for the retention window.

## 3. Encryption

### In transit

- **At go-live —** TLS terminates at the Azure Container Apps ingress, which enforces TLS 1.2+ and
  HTTPS. Azure Blob Storage is provisioned HTTPS-only with a TLS 1.2 minimum.
- **Live —** authentication and antiforgery cookies are `HttpOnly` and `SameSite=Lax`.

Additional host-level transport hardening (strict host filtering, HSTS, a content security policy,
and secure-cookie enforcement) is part of the go-live security work tracked in the engineering
roadmap.

### At rest

- **At go-live —** PostgreSQL, Blob Storage, and Key Vault encrypt data at rest with Azure
  platform-managed keys. Key Vault soft-delete retains deleted secrets for 90 days. Customer-managed
  keys are not used in the beta design.

## 4. Access controls and tenancy

- **Row-level security is the tenant boundary.** Every organization-scoped table has `FORCE ROW LEVEL
SECURITY` and an `org_id` isolation policy applied through one migration helper; a CI schema-guard
  test fails the build if any org-scoped table is missing it. Each authenticated request sets its
  organization context inside a transaction (`set_config('app.org_id', …, is_local => true)`), so the
  setting dies with the transaction and cannot leak across pooled connections. A request with no
  organization context matches no rows — the boundary **fails closed**.
- **Three least-privilege database roles.** A migrator role owns the schema and runs migrations only;
  the runtime application role holds data-manipulation rights but is a non-owner, so row-level
  security binds it; a read-only role serves support and reporting. `PUBLIC` is stripped of schema
  access.
- **Append-only ledger and audit.** The runtime role has no `UPDATE` or `DELETE` grant on the journal
  and audit tables; corrections are linked reversals, never edits or deletions.
- **PM income is structurally invisible to owner-facing reads.** Management-fee income cannot appear
  in an owner statement or export by construction — a trust-accounting invariant, not a display
  filter.
- **Application authorization is deny-by-default.** Endpoints require an explicit authorization
  policy; ASP.NET Identity enforces a password-length floor, account lockout, and multi-factor
  authentication.
- **Secrets via managed identity (at go-live).** Connection strings and role passwords are held in
  Azure Key Vault and read by a user-assigned managed identity granted the Key Vault Secrets User role
  scoped to the vault; the same identity pulls container images.

## 5. Retention

| Data class                   | Mechanism                            | Window                                                                  |
| ---------------------------- | ------------------------------------ | ----------------------------------------------------------------------- |
| PostgreSQL data (all tables) | Point-in-time recovery backups       | Dev 7 days; production 35 days (geographically redundant in production) |
| Application telemetry        | Log Analytics / Application Insights | 30 days                                                                 |
| Deleted Key Vault secrets    | Soft-delete                          | 90 days                                                                 |

The windows above are the technical defaults in the authored infrastructure. Two policy windows are
**not yet established** and are inputs to the compliance review:

- **Trust-record retention minimum — `[LEGAL REVIEW]`.** NCREC Rule 58A .0116 governs how long trust
  records must be retained. The compliance review sets the required minimum; the journal and audit
  trail are append-only and are not purged today, but no enforced retention floor is yet encoded.
- **Artifact and audit-log lifecycle.** Delivered-statement artifacts and `audit_events` have no
  lifecycle policy today (retained indefinitely). A retention and disposal policy for both is a
  tracked gap, to be set consistent with the trust-record minimum above.

## 6. Data lifecycle: onboarding and offboarding

- **Onboarding.** An organization is provisioned as one transactional operation; opening balances are
  imported through the migration toolkit and proven to tie out to the cent before sign-off.
- **Per-organization export.** The design goal is that a departing customer's data leaves with them as
  a complete per-organization export. This whole-organization export is **designed but not yet
  implemented.** Available today are narrower exports: the trust compliance pack (per trust account
  and period), owner statements, and per-report CSV/PDF downloads.
- **Deletion and offboarding.** A documented hard-delete path with retention rules is **designed but
  not yet implemented.** The append-only grant model deliberately prevents routine deletion of ledger
  and audit data, so a compliant offboarding or erasure path requires a privileged, audited,
  out-of-band mechanism and will be recorded in its own ADR. Both the export and the deletion path are
  prerequisites for the beta customer-trust commitment and are tracked as unbuilt work.

## 7. Governance and references

- The safeguards above are enforced by repository invariants and CI, not by convention:
  [architecture](../architecture.md) (row-level security, the three-role model),
  [accounting](../accounting.md) and the posting-integrity ADRs (fiduciary correctness), and
  [SECURITY.md](../../SECURITY.md) (append-only ledger and audit as a security property).
- Existing compliance records: the [ADR-016 addendum](../adr/ADR-016-reporting-read-layer.md) (trust
  compliance pack — a review-packet input) and
  [ADR-014](../adr/ADR-014-reconciliation-engine-and-lock.md)'s revisit trigger, which defers two
  fiduciary policies (interest entitlement on trust funds; trust-fee coverage) to this same review.
- Items marked `[LEGAL REVIEW]` here and in the [privacy-notice draft](privacy-notice-draft.md) are
  the determinations the external review confirms or replaces; each resolved item lands as an ADR
  amendment or a new ADR with tests.
