# ADR-050: Refund checks post at issue and derive their status from the bank register

- **Status:** Accepted
- **Date:** 2026-10-01
- **Deciders:** Jerry Holland

## Context

A tenant's held security deposit or prepaid credit is returned by check. The `RefundIssued` posting
template already existed, but nothing in the product called it, and it took the refund bank from the
caller. A deposit refund drawn on the wrong bank fails its bucket guard, but a prepayment refund was
guarded only against the tenant's total, so a wrong bank could strand liability against the wrong
cash. Nothing recorded a check number, payee or mailing address, and nothing printed a check.

A check is a negotiable instrument that leaves trust cash only when the bank pays it. Its number,
payee, amount, date and purpose belong to the trust record. Staff need to see which checks are still
outstanding, void unpaid ones, and never void one the bank has already paid.

## Decision

**The refund posts when the check is issued.** `RefundIssued` debits the held liability and credits
the trust bank on the issue date. The check is an outstanding withdrawal in the bank register until
it clears through the existing clearance and reconciliation path ([ADR-014](ADR-014-reconciliation-engine-and-lock.md)).
Status is derived, never stored: `outstanding`, `cleared` or `reconciled` comes from the check's bank
line in `bank_line_status`, and `voided` comes from a linked reversal.

**The bank comes from the liability.** Accounting reads the tenant's positive held buckets. A deposit
bucket is the exact bank, property and owner its collection carried
([ADR-026](ADR-026-deposit-disposition-owner-attribution.md)). A prepayment bucket is per bank and has
no owner. With one bucket for the chosen source, that bucket is used. With several, the caller must
name one (`refund_bucket_ambiguous`). The caller never names a bank directly. The `RefundIssued`
template now guards prepayments per bank, matching the per-bucket deposit guard.

**Accounting owns the money, and Payments owns the check.** The check record (`refund_checks`), its
print history and per-bank print calibration live in the Payments module. Payments reaches
Accounting only through its `IRefundCheckLedger` port ([ADR-007](ADR-007-cross-module-read-contracts.md)).
Accounting derives the bank, writes all journal text, guards the void and reports status. A check
entry carries `source_ref = refund-check:{checkId}`, which makes a retried post a duplicate and marks
the entry as a check. The generic entry void refuses such an entry (`refund_check_void_required`).

**Numbers are per bank account and all accounted for.** The issuer supplies the number on the stock
in the printer, prefilled with the next number after that bank's highest refund check. A number is
unique per bank across issued and voided checks. It appears in the posted description
(`Refund check #1043 — security deposit`), so the register, its search and the tenant's ledger show it.
A misprint is voided with a reason and reissued on the next number. A per-organization advisory lock
serializes issue, so the idempotency key and number checks cannot race. The lock is taken before
Accounting's posting lock and never after it.

**Voiding is guarded.** A void posts a linked reversal that restores the held liability. It is
refused once the check's bank line is cleared or reconciled (`refund_check_cleared`). The original
withdrawal and its reversal are then cleared together, because they net to zero and never reach the
bank. A voided check therefore does not linger as an outstanding item.

**Printing targets pre-printed voucher stock.** The organization's bank-issued check stock already
carries the bank, the organization, the check number and the MICR line. LeaseBook prints only the
variable fields on a check-on-top Letter voucher with two stubs, shifted by per-bank-account offsets of
up to an inch. It stores no routing or account numbers. Each print is an audited row, and the PDF
response is not cached. Blank stock with a generated MICR line is a separate decision (#474).

**Compliance assumptions are provisional.** We assume the check record satisfies the NCREC trust
record rules (21 NCAC 58A .0116–.0117, retained under .0108). That record holds the number, payee,
amount, date and purpose, alongside the journal entry and its audit rows. The assumption awaits the
trust-accounting attorney review and is not a verified finding.

## Consequences

Issuing a refund is one form and one post, and the trust equation holds throughout because the
liability and the bank fall together. The register already shows outstanding checks, and reconciling
them needs no new mechanism. Statement auto-match still pairs on amount and date, not check number.

The check record never changes after insert, so its history is its audit rows plus the ledger.
Refunds posted before this change, such as the scenario fixture's, are not check-backed and stay
voidable through the generic void. A stale or unclaimed check is still a manual decision. Nothing
stale-dates a check, reports unclaimed property, or produces a positive-pay file.

## Revisit trigger

Blank-stock printing and the MICR line (#474), once a trigger here, are decided in
[ADR-051](ADR-051-blank-stock-micr-details.md).

Revisit before adopting a print-and-mail vendor, issuing owner or vendor checks, automating stale-dating or escheatment, matching statements
by check number, or when the attorney review changes the check-record requirements.

## Addendum — a prepayment application draws on the bank that holds it (invariant I10) (2026-10-01)

Applying a prepayment still took its bank from the caller, and the apply dialog always sent the
operating trust account. The `PrepaymentApplied` template guarded only the tenant's total, so credit
collected into one bank and applied from another released the liability and credited the owner on a
bank the cash never reached. Neither the trust equation (I2) nor the per-tenant liability check (I4)
sees that: every bank still balances and the tenant's total is right, while one bank's prepayment for
the tenant goes negative and another's stays positive.

**The bank comes from the liability here too.** `ApplyPrepayment`'s bank is optional. Left out,
Accounting derives it from the tenant's positive prepayment buckets, the same read a refund uses,
under the same posting lock. With one bucket that bank is used; with none the application is refused
(`insufficient_liability`); with several the caller must name one (`prepayment_bank_ambiguous`).
Because a prepayment bucket is only a bank, the caller names a bank rather than a bucket, and a named
bank must itself hold the amount: `PrepaymentApplied` now guards the smaller of that bank's held
prepayment and the tenant's total, as `RefundIssued` does for prepayments. Both guards keep the total
because a journal written before this change can hold a positive bucket beside a negative one.

**Invariant I10 sweeps it:** a tenant's held prepayment is ≥ 0 per bank, the prepayment counterpart
of I7 ([ADR-026](ADR-026-deposit-disposition-owner-attribution.md)). An application posted before
this change against a bank that never held the credit fails I10 until it is voided and re-applied
from the holding bank.

## Revisit trigger (addendum)

Revisit if prepaid credit ever needs to move between trust banks. That is a transfer with its own
event, not an application drawn on a bank that does not hold the credit.
