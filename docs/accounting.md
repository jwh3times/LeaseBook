# How LeaseBook keeps the books

This is the plain-English description of the trust-accounting engine — written so a property
manager, bookkeeper, or attorney can check that the software does what the law and the trade
require, without reading C#. The authoritative detail is in `src/LeaseBook.Modules.Accounting`;
the binding rules are in `CLAUDE.md` ("Non-negotiable invariants") and ADR-006.

## The one ledger

Everything is a **double-entry journal**. Each event writes one *entry* made of *lines*; within an
entry the debits equal the credits. Nothing is ever edited or deleted after it is posted — a mistake
is fixed by posting a linked **reversal** (an equal-and-opposite entry), so the history is always the
truth of what happened and when. Every tenant statement, owner statement, bank balance, and deposit
register is a **query over this journal**, never a separately maintained number that could drift.

## The accounts (and why classes matter)

Every line points at an account, and every account has a **class**. The class — not a report filter —
is what keeps fiduciary money straight:

| Class | What it holds |
| --- | --- |
| `trust_bank` | Money physically in a trust bank account (rent, deposits) — held *for* owners/tenants |
| `owner_equity` | How much of the trust belongs to each owner |
| `tenant_receivable` | What a tenant owes (rent, fees) — a promise, not cash |
| `deposit_liability` | Security deposits and prepayments — money we **owe back** until it is applied |
| `pm_income` | The property manager's earned management fee |
| `pm_operating_bank` | The manager's *own* operating bank account |

The crucial separation: **`pm_income` can never carry an owner's name.** It is enforced at the
database level (a line tagged owner-and-PM-income is rejected), so the manager's fee income can never
appear on an owner's statement. Likewise security deposits and prepayments are **liabilities** — they
are not income, and they do not become income until they are actually applied.

## Two bases, one set of books

Every line is tagged **cash**, **accrual**, or **both**:

- *accrual* records what is owed/earned when it happens (rent charged today is income today).
- *cash* records money when it actually moves (rent is income when the tenant pays).
- *both* means the line counts in either view (a deposit hitting the bank is simultaneously real
  cash and a real liability).

Because the basis is just a tag, the cash and accrual views are two readings of the **same** journal —
they can never disagree about the past. (Technical note for implementers: a `both` line belongs to
each basis, so a balance is "this basis plus both," never a blind sum of all three tags.)

## A worked month

A tenant, Jasmine, rents a unit from an owner, Renée's client.

1. **Rent charged ($1,450).** Jasmine now owes $1,450 (receivable up) and the owner has earned $1,450
   (owner equity up). *Accrual only* — no cash has moved yet.
2. **Payment received ($1,450, ACH into the operating trust).** The trust bank goes up $1,450 (cash
   in). That clears her receivable (accrual) and turns into the owner's cash income (cash). If she had
   **overpaid**, only the part covering what she owed would clear the receivable; the rest would be
   booked as a **prepayment liability** — money we hold for her — never as a negative balance.
3. **Management fee assessed ($290).** The owner's equity goes down $290 and the manager's
   `pm_income` goes up $290. The fee is *held in the trust bank* for now (it is the manager's money,
   sitting in the trust account until it is moved out).
4. **Fees swept ($290, trust → the manager's own bank).** The $290 leaves the trust bank and lands in
   the manager's operating bank; the income attribution moves with the cash. Net income is unchanged —
   the money just changed pockets.
5. **Owner disbursed ($8,200).** The owner's equity goes down and the trust bank goes down by the same
   amount. A disbursement is refused if it would take the owner below their configured reserve.

A **security deposit** follows a different rule on purpose: when collected it increases the deposit
trust bank and a deposit *liability* — and books **no income at all**. It only becomes income (or
clears a charge) when it is actually **applied** at move-out, and that recognition is identical in the
cash and accrual views.

## The trust equation (the safety check)

At all times, for every trust bank account:

> **bank book balance = owners' equity + deposit/prepayment liabilities + PM fees held in that bank**

In words: every dollar sitting in a trust account is accounted for as belonging to an owner, owed back
to a tenant, or being a fee not yet swept. If this is ever off by a cent, something is wrong. The
engine tests this continuously, and a `check-invariants` command can verify it for any organization on
demand.

## Fixing mistakes

Posted entries are never altered. To correct one, the system posts a **reversal**: the same lines with
debits and credits swapped, linked back to the original, dated into the current open period (never a
closed one). The original and its reversal net to zero in every report. An entry can be reversed at
most once, and a reversal cannot itself be reversed.

## Closing a period

Two independent locks keep settled history settled. Each month is an accounting **period** that can be
**closed** for the whole organization; the engine then refuses to post into it, and corrections for a
closed month post into the current open month instead. Separately, **reconciling one bank account** for a
month locks *that account's* month (see "Banking" below) — a finer-grained lock that is what month-end
reconciliation actually uses. A post is rejected if either lock covers it.

## Writing entries through the ledger composer

Money first moves through the UI in M3. The tenant-ledger composer never builds journal lines itself —
it sends a small **command** that the server wraps around the existing posting engine, so there is still
exactly one write path to the journal. The command carries only a tenant id plus the amount/date/method/
memo; the owner, property and unit are resolved server-side from the tenant's **active lease** (a post
for a tenant with no active lease is rejected, never guessed). Each command maps to one business event:

| Command (endpoint) | Business event |
| --- | --- |
| `POST /tenants/{id}/payments` | `PaymentReceived` (ACH/Card/Check/Cash) |
| `POST /tenants/{id}/charges` | `RentCharged` (rent) or `FeeCharged` (late / maintenance-recharge / other) |
| `POST /tenants/{id}/credits` | `CreditIssued` |
| `POST /tenants/{id}/deposits` | `DepositCollected` |
| `POST /tenants/{id}/prepayments` | `PrepaymentReceived` |
| `POST /tenants/{id}/deposit-applications` | `DepositApplied` (to owner income, or against charges) |
| `POST /tenants/{id}/prepayment-applications` | `PrepaymentApplied` |
| `POST /entries/{id}/void` | a linked reversal (see "Fixing mistakes") |

Every submit carries a client-minted **idempotency key** (`sourceRef`), so a double-click or retry maps
to "already posted" rather than posting twice. `GET /tenants/{id}/ledger.csv` exports the on-screen
ledger.

**The over-application rule (ADR-011).** A payment that exceeds what the tenant owes auto-splits the
excess into a prepayment. An *application* has no such overflow, so applying a deposit **against charges**
— or applying a prepayment — that would exceed the tenant's open receivable is **rejected**
(`insufficient_receivable`); the composer asks the user to lower the amount. Applying a deposit **to owner
income** (damages) is deliberately *not* capped — damages legitimately exceed any rent owed. This sits
alongside the existing rule that an application can never exceed the deposit/prepayment actually held.

## Who did it

Every posted entry now records the acting user. The authenticated user's id is stamped onto the journal
entry (`created_by`) and onto each `audit_events` row (`actor_user_id`); a reversal carries the user who
voided. The seeder and background jobs write as the system (a null actor, by design). The per-entry audit
trail (`GET /entries/{id}/audit`) returns the entry's and its reversal's rows newest-first, resolving each
actor to a name/email — an org-scoped identity lookup, so one company can never see another's users.

## Banking: the register, clearing & reconciliation (M4)

A **bank register** is the journal seen from one bank account: every line posted to that account, shown
statement-style (deposits = debits, withdrawals = credits), newest first. Like every other ledger it is a
*projection* of the journal — nothing is independently maintained.

**Clearing.** A bank line is `uncleared`, `cleared`, or `reconciled`. That status is operational metadata,
not part of the immutable journal, so it lives in a side table (`bank_line_status`) the runtime may update —
the journal itself stays byte-stable. The register's **book** balance is every line; **cleared** is the
lines marked cleared or reconciled; **uncleared** is the difference. Absence of a status row means uncleared.

**Reconciling.** Reconcile-in-place takes the statement's ending balance and ticks uncleared lines until the
**difference reaches $0.00**, then **finalizes**. Finalize is rejected unless the difference is zero; on
success it marks the ticked lines `reconciled`, stores an **immutable report snapshot**, and **locks that
account for that month**. A later post carrying that bank account dated into a locked month is rejected
(`account_period_locked`) and surfaced in the composer the way an over-application is. A `PMAdmin` can
**unlock** a finalized month with a reason (written to the audit log); items stay reconciled until the month
is finalized again. There is no bulk un-reconcile.

**Bank-only adjustments.** Three posting templates cover the statement lines reconciliation needs that are
not tenant/owner activity: `BankFeeCharged` (a service charge — a PM operating cost), `InterestEarned`
(interest credited to the trust), and `TrustTransfer` (moving funds between two of the org's own bank
accounts). Each balances per basis and posts through the single journal write path; none implies an
owner/vendor/fee-sweep workflow (those are M6).

**Statement import.** A bank CSV can be imported (column-mapped, with saved per-bank mappings) and
auto-matched against uncleared register lines: an exact amount on a nearby date is a confident match that
clears the line on confirm; an exact amount on a far date is a suggestion; no amount match offers to create a
transaction. Re-importing the same statement is de-duplicated, never double-counted. Matching and clearing
always run through the accounting engine — the importer never writes the journal directly.
