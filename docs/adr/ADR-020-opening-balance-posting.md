# ADR-020 — Balance-Forward Opening-Balance Posting Model (M7)

- **Status:** Accepted
- **Date:** 2026-06-23
- **Deciders:** Engineering

## Context

M7 must post opening balances from an AppFolio export onto a clean LeaseBook org. The existing
`IBalanceForward` contract — used by `DemoJournalSeed` since M1 — takes a pre-balanced line set
(all basis `both`, caller must balance) and posts it as one entry. That works for hand-authored seed
data where the figures are known to tie.

A real AppFolio import may **not** tie: an owner balance total, a deposit register total, a bank
book balance, and a receivable balance imported from four separate CSV files may disagree by one
cent or by thousands of dollars. The existing batched method would either require the caller to
pre-prove correctness (which is the thing being verified) or to absorb a non-tying set into a
single entry that the engine would reject (`Σ debits ≠ Σ credits`).

Three approaches were evaluated:

**A (chosen). Extend `IBalanceForward` with a per-position, clearing-contra method; add
`AccountClass.MigrationClearing`.** Each imported position posts one self-balancing entry (real
account leg + equal-and-opposite `MigrationClearing` contra). A non-tying import accumulates a
clearing residual instead of failing; the residual is the quantified discrepancy, surfaced by the
verification report. Leave the batched method and the demo golden untouched.

**B. Keep the batched method; require the caller to pre-balance.** Requires computing the balancing
figure before posting, which is algebraically equivalent to asserting correctness before posting —
the very assertion the verification step is supposed to make. The "pre-balance" is a fake computed
line, not real data, and it would mask discrepancies rather than surface them.

**C. A separate `opening_balances` snapshot table outside the journal.** Violates the core invariant
("every balance is a projection of the journal — never an independently maintained number"). Any
query that computes a balance from the journal would diverge from the snapshot if any posting
template changes.

## Decision

**Approach A.** Extend `IBalanceForward` with:

```csharp
Task<Guid> PostOpeningPositionAsync(OpeningPositionRequest req, CancellationToken ct);
```

Each call posts **one self-balancing entry** containing two lines: the real account leg and an
equal-and-opposite `MigrationClearing` contra leg, both tagged `req.Basis`. The entry is dated at
`req.Cutover` and keyed by `req.SourceRef` for idempotency.

**`AccountClass.MigrationClearing`** is added as a seventh account class alongside the existing six.
It is not a flag or filter — it is a structural class that keeps the clearing account out of all
owner-statement queries (which select `account_class = 'owner_equity'`) and outside the trust
equation (computed from `trust_bank`, `owner_equity`, `deposit_liability`, `pm_income`). It is a
transient cutover suspense account whose correct post-go-live balance is $0.00 in both bases.

**Per-basis line construction.** The import host (`BalanceImportService`) maps each imported
position to the correct account, normal side, and basis tag:

| Imported position       | Account class        | Normal side | Basis tag   |
|-------------------------|----------------------|-------------|-------------|
| Trust bank book balance | `trust_bank`         | Debit       | `both`      |
| Owner equity (cash)     | `owner_equity`       | Credit      | `both`      |
| Owner equity accrual delta | `owner_equity`    | Credit      | `accrual`   |
| Deposit liability       | `deposit_liability`  | Credit      | `both`      |
| Tenant receivable       | `tenant_receivable`  | Debit       | `accrual`   |

The `MigrationClearing` contra leg mirrors the real leg: if the real leg is DR $1,200, the contra
is CR $1,200 (same basis, same amount). A non-positive figure ($0.00) is a no-op — skipped, never
sent to the posting service.

**Clearing nets to $0 iff the import ties.** After all rows post, the `MigrationClearing` balance
per basis equals `Σ(debit-side imports) − Σ(credit-side imports)`. Algebraically, in the cash basis
this is the trust equation (`bank = owner_equity + deposit_liability + pm_fees`); in the accrual
basis it additionally includes receivable/accrued-income. A correct, internally-consistent import
drives clearing to **$0.00 in both bases**. Any residual is a quantified, dimensioned discrepancy
that the verification report surfaces and the sign-off gate blocks on.

**Idempotency.** Each opening line carries a deterministic `source_ref`:
`opening:{cutover:yyyy-MM-dd}:{type}={subledgerId}` (accrual-delta rows use a distinct suffix
`owner-equity-accrual=`). The existing `(org_id, source_ref) WHERE source_ref IS NOT NULL` partial
unique index on `journal_entries` and `DuplicateSourceRefException` from `PostingService` are the
backstop — no new index. Re-importing the same batch is an idempotent no-op.

**The batched method and the demo golden are untouched.** `IBalanceForward.PostAsync(
BalanceForwardRequest)` and `DemoJournalSeed`'s hand-tied opening figures remain byte-identical.
The demo org models an already-migrated org; the new per-position path is exercised only by the
M7 synthetic cutover fixture and production imports. This separation is deliberate: the batched
method is an internal convenience for hand-authored seeds; the per-position method is the
correctness-checking path for real imports.

The `AddImportToolkit` migration adds `migration_clearing` to the `account_class` DB CHECK on
`accounts` and the denormalized `journal_lines.account_class` column. A new standing invariant
(`MigrationClearing == $0.00` per basis, asserted by `check-invariants`) fires on any org that has
completed sign-off.

## Consequences

- The clearing-account design turns "does this import tie?" from a procedure into a structural
  property: zero clearing residual IS the proof. The verification report reads it from the journal
  directly via `GetMigrationVerificationData` (no separate computation).
- A non-tying import does not produce a posting failure — it produces a clearing residual that is
  surfaced, not suppressed. The discrepancy is quantified and dimensioned (per-basis, per-account).
- `AccountClass.MigrationClearing` is a permanent addition to the enum and the DB CHECK. A
  correctly migrated org will carry zero-balance clearing accounts forever. They do not appear on
  any owner-facing view and they do not affect the trust equation.
- The batched `IBalanceForward.PostAsync` path is now a legacy seam for demo/test seeds. Any new
  production import should go through `PostOpeningPositionAsync`. This distinction is enforced by
  convention, not by the compiler.

## Revisit trigger

If a future import source (Buildium, Rentec) requires per-position semantics that differ materially
from the AppFolio model (e.g., a source that already supplies a pre-balanced line set per position
as a verified export artifact), re-evaluate whether the per-position method should accept an
optional pre-computed contra or whether a second overload is the right shape.
