# ADR-006: Posting-template catalog & dual-basis journal

- **Status:** Accepted
- **Date:** 2026-06-12
- **Deciders:** Engineering

## Context

Trust accounting is the product's differentiation, and its correctness has to be encoded in one
auditable place, not scattered across feature code. Two forces shape how:

- The PRD requires **both cash and accrual** views, and the NC fiduciary standard requires that a
  basis be reportable at any time without re-deriving history.
- Every money movement must be **balanced, append-only, and isolated** (PM income from owner income;
  one owner's trust funds from another's) — invariants, not preferences (CLAUDE.md).

The question is how business events ("rent charged", "payment received", "owner disbursed") become
journal rows, and how the two bases coexist.

## Decision

- **Templates are versioned C#, one per business event** (`Features/Posting/Events` +
  `AccountingEventService`), posted through the single `IPostingService` write path — *not*
  DB-configurable rows. A wrong posting is a correctness bug we want to catch in code review and
  unit tests, not a data-entry mistake in a config table. Each template's exact line set is pinned
  by a worked-example test, and a catalog-wide CsCheck property proves every event balances per
  basis by construction.

- **Dual basis is a line tag, not a transformation.** Each line carries `basis ∈ cash·accrual·both`;
  a `both` line participates in *each* basis. A basis report is then a query
  (`WHERE basis IN (@basis,'both')`), never a conversion pass. Consequence the code must respect:
  summing across all three tags double-counts the `both` set (pitfall M-E2), so balance/ledger
  queries always filter to exactly one requested basis plus `both`.

- **Two templates were added beyond the PRD's narrative list:**
  - `BalanceForward` — cutover/opening positions (all lines `both`). M7 import needs it and the demo
    dataset cannot reconcile without it. Exposed only via a separate `IBalanceForward` contract
    consumed by seed/import code, never by the product's event flows.
  - `PMFeesSwept` — moves held management fees from the operating trust to the PM's own operating
    bank. Without it the trust equation cannot survive a fee transfer (the cash leaves trust but the
    income attribution must move with it).
  `EntryVoided` is reserved for the reversal service and is never posted directly.

- **Overpayment auto-splits (P31).** `PaymentReceived` posts the portion up to the tenant's open
  receivable against the receivable, and any excess to the `tenant_prepayments` **liability** — never
  a negative receivable. Income is recognised only on application, identically in both bases.

- **Guarded events serialise per org with a transaction advisory lock (P31).** Events that read a
  balance and then post against it — the auto-split, deposit/prepayment over-application checks, the
  PM-fee over-sweep check, and the disbursement reserve floor — take
  `pg_advisory_xact_lock(hashtextextended('lb:acct:' || org_id, 0))` before the read, so two
  concurrent transactions cannot both act on a stale balance (pitfall M-E7). The lock dies with the
  transaction.

- **Bank attribution on every funds-backing line (P36).** Each line that moves or backs bank funds —
  including the liability/equity counter-lines — carries the trust account's `bank_account_id`, which
  is what makes the trust equation computable per bank rather than org-wide.

## Consequences

- The founder's domain knowledge lives in ~15 reviewable templates with verbatim worked-example
  tests; an attorney/accountant review has one file (`docs/accounting.md`) and one test file to read.
- Reports are queries over an immutable journal, so cash↔accrual can never disagree about history.
- We accept some verbosity (every line spells out its basis and dims) and the discipline that balance
  queries must be basis-correct; both are enforced by tests, not convention.
- **Hangfire stays deferred (P33).** ADR-001 chose Hangfire, but M1 has no recurring job: the
  invariant sweep ships as a domain service + `check-invariants` CLI verb, runnable by hand/CI. The
  first milestone that needs a *scheduled* job wires Hangfire to `OrgScopedExecutor`. This is a timing
  call, not a new default.
- **Period auto-create can mask a wrong date (M-E10):** posting into a far-future month silently opens
  that period. Acceptable in M1 (no UI surface); the M3 composer adds date-sanity validation.

## Revisit trigger

If posting rules ever need to vary **per org** (e.g. configurable late-fee splits, jurisdiction-
specific recognition) such that code-per-event no longer fits, revisit a data-driven template engine
— with the same test rigor applied to the configuration.
