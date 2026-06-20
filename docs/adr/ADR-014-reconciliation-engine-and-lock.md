# ADR-014: Bank reconciliation — engine placement, clearance model, and the hybrid lock

- **Status:** Accepted
- **Date:** 2026-06-20
- **Deciders:** Engineering

## Context

M4 adds the bank side of the trust story: a register, a reconcile-in-place workflow that drives the
difference to $0 and locks the reconciled account-month, and three bank-only posting templates
(fee/interest/transfer). Several decisions needed recording because they touch module boundaries, the
append-only invariant, and the continuously-tested trust equation.

## Decisions

### 1. The register and reconciliation engine live in `Modules.Accounting`

A bank register is a **read-model projection of the journal** (CLAUDE.md), the same family as the
existing `GetBankBalances` / `GetTenantLedger` / `GetOwnerLedger` reads, and reconciliation finalize is
a **posting-guard** concern — both things Accounting already owns. So clearance state, the register
query, the reconciliation aggregate, the per-account lock, and the immutable report all live in
Accounting; the read SQL is own-module (crosses no boundary, ADR-007). `Modules.Banking` (M4 WP-05)
owns **statement import + auto-match** and reaches the register/clearances through ADR-007 ports.

### 2. Clearance status is a side table over the append-only journal

Cleared/uncleared/reconciled status is **mutable**, but journal rows are append-only (the runtime role
has no UPDATE on journal tables). So status lives in **`bank_line_status`** (`journal_line_id` PK/FK,
`org_id`, `status`, `cleared_at`, `reconciliation_id`), org-scoped via the RLS helper, **lazily
upserted** — absence of a row ≡ `uncleared`. It is operational metadata, not a journal row, so the
runtime role **keeps INSERT/UPDATE** on it (no `RevokeAppendOnly`). The journal and its golden figures
stay byte-stable. The register reads it through a `LEFT JOIN`; clearing/un-clearing and finalize are
raw upserts. FKs *into* `journal_lines` stay single-column (the journal PK is globally unique, P61);
the FK from `bank_reconciliations` to `bank_accounts` is composite `(org_id, bank_account_id)` (P60).

### 3. The register is bank-**account** lines, not the bank dimension

Many journal lines carry a `bank_account_id` *dimension* for attribution (an owner-equity cash line, a
pm-income line). Only lines whose **account class** is `trust_bank`/`pm_operating_bank` move the bank
book, so the register, the cleared-balance math, and the posting lock all filter on `account_class`,
never on the dimension alone.

### 4. Hybrid lock: a finalized reconciliation IS the per-account lock

Reconciliation is per `(org, bank account, year, month)`. **Finalize requires a zero difference**
(`statement_ending_balance − cleared_balance == 0`); it marks the account's cleared lines `reconciled`,
stores an immutable report snapshot, and — because a **`finalized` row for (account, month) is the
lock** — closes that account-month to new bank postings. The posting service consults
`IReconciliationLock` for every **bank-account** line, alongside the existing per-org
`AccountingPeriod` close. Two independent gates: `account_period_locked` (409, this ADR) and
`period_closed` (409, M1) — the per-org close stays a separate "close the books" concept that M5/M6
lean on. **Unlock** is `PMAdmin` + reason: it flips `finalized → reopened`, releasing the lock; the
status change is audited with the acting user and carries the reason. Reconciled items stay reconciled
until a re-finalize.

### 5. Bank adjustments move the PM's own held funds (the fiduciary-safe model)

The trust equation (`bank book = Σ owner equity + Σ deposit liabilities + Σ held PM fees`) is
continuously tested, so a bank adjustment that changed a trust account's book without a matching
attribution change would break it. The three templates therefore move the **PM's own held funds**
(`pm_income` tagged to the bank), never owner or deposit money:

- **`BankFeeCharged`** — `DR pm_income@bank / CR bank`: the PM covers the fee from its held funds in
  that bank (held fees ↓, bank ↓). Owners/tenants untouched; trust equation balanced.
- **`InterestEarned`** — `DR bank / CR pm_income@bank`: interest accrues to the PM's held position
  (bank ↑, held fees ↑).
- **`TrustTransfer`** — `DR toBank / CR fromBank` + `DR pm_income@fromBank / CR pm_income@toBank`: cash
  and its held-fee attribution move together, so each account's equation stays balanced.

Two policy questions are **deferred** (recorded here, not resolved): (a) interest *entitlement* on
trust funds (PM vs owner vs a state housing fund) — M4 credits the PM's held position; (b) the
operational rule that a trust bank fee must be covered by sufficient held PM fees (the PM keeping trust
whole) is enforced procedurally, not structurally, in Phase 1 — the posting balances regardless.

## Consequences

- The register/reconcile engine is testable through the accounting harness; the trust equation is
  proven to hold for every adjustment template; the lock is reachable and surfaced (the M3 composer
  shows `account_period_locked` the way it shows `insufficient_receivable`).
- `Modules.Banking` can build import/match on top of stable register + clearance ports.
- The deferred fiduciary policies (interest entitlement, trust-fee coverage) are flagged for a later
  milestone alongside the deposit-disposition/refund work.

## Revisit trigger

When trust-account interest handling or trust-fee coverage becomes a compliance requirement (NCREC
review, M8), replace the deferred defaults with explicit policy and record the change. If transfers of
*owner* or *deposit* funds between accounts ever become a real workflow, they need their own template —
`TrustTransfer` deliberately moves only PM-held funds.
