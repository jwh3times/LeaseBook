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
raw upserts. FKs _into_ `journal_lines` stay single-column (the journal PK is globally unique, P61);
the FK from `bank_reconciliations` to `bank_accounts` is composite `(org_id, bank_account_id)` (P60).

### 3. The register is bank-**account** lines, not the bank dimension

Many journal lines carry a `bank_account_id` _dimension_ for attribution (an owner-equity cash line, a
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
until a re-finalize. Which lines a finalize counts and stamps, and the order an account's months
finalize and unlock in, are narrowed by the addendum below on a month's own statement.

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

Two policy questions are **deferred** (recorded here, not resolved): (a) interest _entitlement_ on
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
_owner_ or _deposit_ funds between accounts ever become a real workflow, they need their own template —
`TrustTransfer` deliberately moves only PM-held funds.

## Addendum — the engine enforces the held-fees floor, and a transfer is trust to trust (invariant I11) (2026-11-06)

Decision 5(b) left the rule that a bank fee must be covered by the PM's held fees to procedure. Nothing
enforced it, and nothing reported a breach: a fee, or a transfer out of a bank, larger than the fees
held there still balanced and still left the trust equation holding, with a negative held-fees term
standing for money owed to the owners and tenants whose funds the bank also holds. While no screen
recorded either event that stayed theoretical. A screen on the bank register now records both (#553),
so the rule moves into the engine.

**The held-fees floor.** `BankFeeCharged`, and `TrustTransfer` against its source bank, are refused
with `pm_fees_insufficient` (409) when the amount exceeds the lowest PM fees held in that bank on or
after the entry's date, under the posting lock. That is the floor the processor fee shortfall already
reads ([ADR-053](ADR-053-processor-fees-and-batched-settlement.md)): a backdated charge lowers every
day after it, so a balance that has since recovered can still have been too low then. A transfer's
destination only gains, so it is not read. The code is unchanged, so Payments still sends a payout to
staff review on it; the message now names the charge it refused, and for a bank fee or a transfer says
how much was held. A posting into a closed period or a reconciled month is refused as that first,
before the floor is read, because no amount of held fees makes it postable.

**A transfer is trust to trust.** `TrustTransfer` is refused with `transfer_bank_not_trust` (422) when
either end is the PM operating bank. Into the operating bank it posted exactly the lines of
`PMFeesSwept` without the sweep's guard; out of it, it was the PM funding trust with its own money,
which needs its own event (#720), not a transfer. The check is in the template, so a direct API call
meets it too.

**Invariant I11 sweeps it:** held PM fees are ≥ 0 in every bank, trust or operating, as they stand
now. It runs in the core set, so the nightly sweep and `check-invariants` both report a journal that
went negative before this change. None of the seeded organizations does.

Interest is unchanged: the template and the `interest` kind stay, and no screen offers it until #307
settles entitlement, which remains deferred under 5(a). This records an engine rule, not a compliance
finding: whether it is what 21 NCAC 58A .0116-.0117 requires is for the compliance review.

## Addendum — a PM contribution funds the held fees, and a staff void keeps to the floor (2026-11-09)

The floor above left a charge larger than the PM's held fees with nowhere to go: in practice the
property manager covers it by depositing its own money into the trust account, and no event recorded
that (#720).

**The model.** `PMContribution` is `DR bank / CR pm_income@bank`, both bases, with the bank dimension
only. The PM's own money joins its held fees in that bank, so the bank and the held fees rise together
and the trust equation holds. It is not a separate balance: held PM fees stay one figure per bank, and a
later sweep moves PM money out whichever way it came in. It posts no owner equity, so it has no owner
statement section. It is a trust-bank event only (`contribution_bank_not_trust`, 422): into the PM
operating bank it would be the PM paying itself. The alternative, a new account class for contributed
funds, was rejected: every held-fees reader (I2, I3, I11, the floor, the trust equation report,
migration verification) would have had to learn it, for a distinction no rule yet asks for.

**It is not income.** The management-fee income report, the one reader that treats `pm_income` as
earned income, leaves out `PMContribution` and its voids, as it leaves out opening positions. Every
other reader keeps reading `pm_income` as a balance and includes it.

**The floor stays strict.** A contribution covers a charge dated on or after it. A bank that charged its
fee before the PM's deposit arrived is recorded with the fee dated on or after the contribution and the
bank's date in the internal note, rather than letting held fees sit below zero between the two dates.
Each `pm_fees_insufficient` message now says that a contribution dated on or before the charge covers it.

**A staff void keeps to the floor.** Before this, voiding an entry that raised a bank's held fees (a
management fee since swept to the PM operating bank, or now a contribution a fee has drawn on) could
take them below zero with no refusal. `VoidEntry` now refuses with `pm_fees_insufficient` (409), naming
the bank and the shortfall, when the void's mirrored lines would leave held fees in any bank below zero
on its date or later, read under the posting lock. It sits in the staff void command, not in the shared
reversal service: the service's other callers either carry no held-fee lines (payment returns, refund
check voids) or repost the corrected position at once (the onboarding supersede of an opening
position, which migration verification checks). Voids that leave held fees alone or raise them are
unaffected.

The floor reads every bank the void takes held fees from, the PM operating bank included: voiding a
sweep takes back fees that bank received, and I11 already holds it to zero. There the refusal offers
no contribution, since one cannot go into that bank. A void into a closed period, or into a reconciled
month of a bank it takes from, is refused as that first, as a bank fee is, because no contribution can
be dated there.

**No cap, provisionally.** Whether NCREC rules limit how much of a broker's own money may sit in a
trust account is a question for the compliance review; this is built without a cap and revisited on
its answer.

## Addendum — a month reconciles to its own statement, and an account's months finalize in order (2026-11-09)

Decision 4 let an administrator unlock a finalized month and left the re-finalize to the engine, but
its arithmetic had no month in it (#656). The cleared balance summed every cleared or reconciled line
on the account, and finalize stamped every cleared line. A past month re-finalized later could reach a
zero difference only at today's cleared balance, never at its own statement, and it stamped the lines
of every month since as its own. Nothing kept an account's months in order, so a later month could be
finalized while an earlier one stayed open.

Issue #656 implements it: the engine rules, the finalize-again report, invariant I12, and the history
row's **Finalize again** view, which ticks the lines finalize takes. Building it settled
one point this addendum left implicit: only the organization's current month may be finalized without
naming its lines. Any other month names them (`reconciliation_lines_required`, 422), because assigning a
past month every cleared line dated through its end is the date bound rejected below.

**A month's cleared balance.** It is the bank lines stamped by that account's reconciliations for that
month or an earlier one, plus the cleared, unreconciled lines assigned to the month while it is being
finalized. Finalize stamps only the assigned lines. Only a line dated on or before the month's last day
can be assigned to it. A line's date does not decide its month: a check written on 28 April and paid on
3 May is assigned to May, the statement that shows it paid. A date bound ("lines dated through the
month's end") was rejected for that reason: it would count the April check in April, against a
statement that never showed it. This rule applies to every finalize. Finalizing the current month
assigns every cleared, unreconciled line dated through its last day, which in practice is what it
stamped before, since a bank line cannot be dated after the organization's today.

**Finalizing a reopened month again.** A reopened row in the reconciliation history offers to finalize
it again, which opens that month's reconcile view in place. The statement ending balance is prefilled
from the one stored and can be changed, and a change is audited. The view offers every unreconciled
line dated through the month's last day, cleared or not, with none ticked, and shows the difference as
lines are ticked. Ticking an uncleared line clears it and assigns it. Lines the month stamped when it
was first finalized stay stamped, as decision 4 says. Staff may finalize it again, as they may finalize
any month: the administrator's decision, and its reason, is the unlock.

**An account's months finalize in order.** For each bank account:

- finalizing a month is refused while a later month is finalized
  (`reconciliation_later_month_finalized`, 409);
- finalizing a month is refused while an earlier month is reopened
  (`reconciliation_earlier_month_reopened`, 409);
- unlocking a month is refused while a later month is finalized
  (`reconciliation_later_month_finalized`, 409).

Each refusal names the month to act on. To correct April when May and June are finalized, an
administrator unlocks June, then May, then April; April is corrected and finalized, then May, then
June. Each later month's cleared balance then includes the correction, as its real statement does. A
month never reconciled does not block a later one: an account may start reconciling at any month, and
a skipped month can still be reconciled until a later one is finalized.

**No future month.** Starting or finalizing a reconciliation for a month after the organization's
current month is refused with `reconciliation_period_in_future` (422), read in the organization's zone
([ADR-037](ADR-037-effective-dated-lease-attribution.md)).

**The report.** Finalizing a month again replaces its stored report. The report it replaces stays in
the audit trail's before-image. The new report records when the month was first finalized, who
reopened it and why, and when it was finalized again.

**Invariant I12 sweeps it.** For every finalized reconciliation, (a) its cleared balance, recomputed
under the rule above, equals its statement ending balance; and (b) no line it stamped is dated after its
month's last day. It runs in the core set, so the nightly sweep and `check-invariants` both report a
month that stopped balancing. No stored data is rewritten. The seeded organizations finalize their
months in order and stamp only lines dated through each month, so their golden figures are expected
to be unchanged. The implementation proves that by replaying all three, and any report snapshot that
moves does so deliberately, in the same change. Replaying them found one exception: the demo
organization's finalized April 2026 Operating Trust row was written directly by its seeder and stamped
no lines, so I12 reported it. The seeder now stamps the four cleared lines dated through April with it,
which nets April to its 250,450.14 statement; no posted figure moves, only those lines' status.

## Addendum — a fee sweep keeps to the held-fees floor, and staff record one from the register (2026-11-13)

Since a transfer cannot go into the PM operating bank (the first addendum), `PMFeesSwept` is the only
way the property manager's earned fees leave trust, and only the seeders posted one (#743). Its guard
also predated the floor: it refused a sweep larger than the fees held in the bank **today**, with
`insufficient_liability`, so a backdated sweep could take an earlier day's held fees below zero while
today's balance had since recovered. Invariant I11 reads today's balance only, so nothing reported it.

**The floor rule names the sweep.** `PMFeesSwept` is refused with `pm_fees_insufficient` (409) when its
amount exceeds the lowest PM fees held in its source bank on or after its date, read under the posting
lock: the same floor, code and lock as a bank fee and a transfer out of a bank. Its message says how
much was held and that a smaller sweep from that date on posts; it offers no contribution, since
sweeping a contribution straight back out would be pointless. `insufficient_liability` no longer
answers a sweep. A sweep into a closed period or a reconciled month of either bank is refused as that
first, before the floor is read, as a bank fee is.

**A sweep leaves trust for the PM operating bank.** Its source is a trust bank, operating or
security-deposit, and its destination a PM operating bank; anything else is refused with
`sweep_bank_invalid` (422) naming the end at fault. Between two trust banks it is a transfer, and the
PM operating bank holds no fees for a sweep to take. Before, a wrong bank at either end failed as an
unknown account, because the template named the two account codes outright.

**Staff record it.** The bank register's dialog on a trust bank offers **Sweep PM fees**, never on the
PM operating bank. It defaults the PM operating account when the organization has one, dates the sweep
the organization's today, and prefills the amount with the floor at the chosen date, read from
`GET /api/accounting/banks/{bankAccountId}/held-fees`. That read returns today's held fees and the
floor together and changes nothing; the posting guard, not the prefill, is the control. Staff
(`RequirePMStaff`) may sweep, as they may record any other bank-register write.

A schedule, a cap, or any rule on how often fees must leave trust is not decided here: that is for the
compliance review. I11 still reads today's balance; the floor is enforced at posting time.
