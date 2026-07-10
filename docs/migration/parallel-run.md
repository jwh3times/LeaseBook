# Parallel-Run Checklist

During the overlap month — from your cutover date until you are fully live on LeaseBook — enter
transactions in **both** AppFolio and LeaseBook so you have a reconciled month-end to compare.
This document is the operator checklist for that overlap period.

---

## During the overlap month — enter in BOTH systems

For every transaction that occurs after the cutover date:

- [ ] **Rent received** — record the payment in AppFolio AND post it in LeaseBook (tenant ledger >
  Apply Payment).
- [ ] **Owner disbursements** — record the disbursement in AppFolio AND post it in LeaseBook
  (owner ledger > Disburse).
- [ ] **Management fees** — charge the fee in both systems on the same date.
- [ ] **Security deposit movements** — apply or refund deposits in both systems simultaneously.
- [ ] **Maintenance / expense payments** — record in both if they touch the trust account.
- [ ] **Bank deposits and withdrawals** — clear items in the LeaseBook bank register as they clear
  your actual bank, the same way you would in AppFolio.

Tip: batch your daily entry at the same time you close out AppFolio. This keeps the two systems
in sync and makes month-end comparison fast.

---

## Month-end comparison — tie these figures to the cent

At the end of the overlap month, pull the following figures from AppFolio (as of the last day of
the month) and compare them to the LeaseBook equivalents.

| Figure | AppFolio source | LeaseBook source | Must match |
|---|---|---|---|
| Owner ending balances (per owner) | Owner Statement — Ending Balance column | Owner detail page — ending balance | Yes, to the cent |
| Total deposit liabilities | Security Deposit Liability report | Dashboard — Deposit Liabilities KPI | Yes, to the cent |
| Bank book balance (per trust account) | Bank Account Detail — Book Balance | Banking register — book balance | Yes, to the cent |
| Tenant balance due (per tenant) | Tenant Balance / Aged Receivables | Tenant ledger — balance | Yes, to the cent |

**If any figure is off:**

1. Identify the first date the two systems diverge (binary search month-end vs. mid-month).
2. Find the transaction that is missing or double-counted in LeaseBook.
3. Correct the LeaseBook entry (void and re-enter, or apply a reversal if already posted).
4. Re-compare the affected figures before signing off.

---

## Sign-off criteria

You are ready to go fully live on LeaseBook (and stop entering transactions in AppFolio) when:

- [ ] All figures in the table above tie to the cent for the overlap month.
- [ ] The LeaseBook migration verification screen shows **Tied** with $0.00 variance (sign-off
  was completed during onboarding, not at the end of the overlap month).
- [ ] You have reconciled at least one bank account in LeaseBook for the overlap month.
- [ ] You are comfortable with the LeaseBook workflow for daily operations.

Once these are met, stop entering data in AppFolio. LeaseBook is your system of record.

---

## Reference

- In-app checklist: **Migration Setup > Reconcile first month** (final onboarding step) or
  navigate directly to `/onboarding/parallel-run`.
- Engine design: `docs/adr/ADR-020-opening-balance-posting.md`,
  `docs/adr/ADR-021-migration-toolkit.md`.
- Import profile behavior: `src/LeaseBook.Migrator/Profiles/AppFolioProfiles.cs`.
