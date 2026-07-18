# How LeaseBook keeps the books

- **Audience:** Contributors, operators, and reviewers
- **Status:** Living accounting guide
- **Owner:** Maintainers
- **Last reviewed:** 2026-07-09

This is the canonical public explanation of the shipped trust-accounting model, written so a
property manager, bookkeeper, or attorney can evaluate it without reading C#. The Accounting module
and its invariant, property-based, and golden-file suites are the executable truth; accepted
accounting ADRs record why the model takes its current shape. Cross-agent implementation constraints
live in [`AGENTS.md`](../AGENTS.md).

## The one ledger

Everything is a **double-entry journal**. Each event writes one _entry_ made of _lines_; within an
entry the debits equal the credits. Nothing is ever edited or deleted after it is posted — a mistake
is fixed by posting a linked **reversal** (an equal-and-opposite entry), so the history is always the
truth of what happened and when. Every tenant statement, owner statement, bank balance, and deposit
register is a **query over this journal**, never a separately maintained number that could drift.

## The accounts (and why classes matter)

Every line points at an account, and every account has a **class**. The class — not a report filter —
is what keeps fiduciary money straight:

| Class               | What it holds                                                                         |
| ------------------- | ------------------------------------------------------------------------------------- |
| `trust_bank`        | Money physically in a trust bank account (rent, deposits) — held _for_ owners/tenants |
| `owner_equity`      | How much of the trust belongs to each owner                                           |
| `tenant_receivable` | What a tenant owes (rent, fees) — a promise, not cash                                 |
| `deposit_liability` | Security deposits and prepayments — money we **owe back** until it is applied         |
| `pm_income`         | The property manager's earned management fee                                          |
| `pm_operating_bank` | The manager's _own_ operating bank account                                            |

The crucial separation: **`pm_income` can never carry an owner's name.** It is enforced at the
database level (a line tagged owner-and-PM-income is rejected), so the manager's fee income can never
appear on an owner's statement. Likewise security deposits and prepayments are **liabilities** — they
are not income, and they do not become income until they are actually applied.

## Two bases, one set of books

Every line is tagged **cash**, **accrual**, or **both**:

- _accrual_ records what is owed/earned when it happens (rent charged today is income today).
- _cash_ records money when it actually moves (rent is income when the tenant pays).
- _both_ means the line counts in either view (a deposit hitting the bank is simultaneously real
  cash and a real liability).

Because the basis is just a tag, the cash and accrual views are two readings of the **same** journal —
they can never disagree about the past. (Technical note for implementers: a `both` line belongs to
each basis, so a balance is "this basis plus both," never a blind sum of all three tags.)

## A worked month

A tenant, Jasmine, rents a unit from an owner, Renée's client.

1. **Rent charged ($1,450).** Jasmine now owes $1,450 (receivable up) and the owner has earned $1,450
   (owner equity up). _Accrual only_ — no cash has moved yet.
2. **Payment received ($1,450, ACH into the operating trust).** The trust bank goes up $1,450 (cash
   in). That clears her receivable (accrual) and turns into the owner's cash income (cash). If she had
   **overpaid**, only the part covering what she owed would clear the receivable; the rest would be
   booked as a **prepayment liability** — money we hold for her — never as a negative balance.
3. **Management fee assessed ($290).** The owner's equity goes down $290 and the manager's
   `pm_income` goes up $290. The fee is _held in the trust bank_ for now (it is the manager's money,
   sitting in the trust account until it is moved out).
4. **Fees swept ($290, trust → the manager's own bank).** The $290 leaves the trust bank and lands in
   the manager's operating bank; the income attribution moves with the cash. Net income is unchanged —
   the money just changed pockets.
5. **Owner disbursed ($8,200).** The owner's equity goes down and the trust bank goes down by the same
   amount. A disbursement is refused if it would take the owner below their configured reserve.

A **security deposit** follows a different rule on purpose: when collected it increases the deposit
trust bank and a deposit _liability_ — and books **no income at all**. It only becomes income (or
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
month locks _that account's_ month (see "Banking" below) — a finer-grained lock that is what month-end
reconciliation actually uses. A post is rejected if either lock covers it.

## Writing entries through the ledger composer

Money first moves through the UI in M3. The tenant-ledger composer never builds journal lines itself —
it sends a small **command** that the server wraps around the existing posting engine, so there is still
exactly one write path to the journal. The command carries only a tenant id plus the amount/date/method/
memo; the owner, property and unit are resolved server-side from the tenant's **active lease** (a post
for a tenant with no active lease is rejected, never guessed). Each command maps to one business event:

| Command (endpoint)                           | Business event                                                             |
| -------------------------------------------- | -------------------------------------------------------------------------- |
| `POST /tenants/{id}/payments`                | `PaymentReceived` (ACH/Card/Check/Cash)                                    |
| `POST /tenants/{id}/charges`                 | `RentCharged` (rent) or `FeeCharged` (late / maintenance-recharge / other) |
| `POST /tenants/{id}/credits`                 | `CreditIssued`                                                             |
| `POST /tenants/{id}/deposits`                | `DepositCollected`                                                         |
| `POST /tenants/{id}/prepayments`             | `PrepaymentReceived`                                                       |
| `POST /tenants/{id}/deposit-applications`    | `DepositApplied` (to owner income, or against charges)                     |
| `POST /tenants/{id}/prepayment-applications` | `PrepaymentApplied`                                                        |
| `POST /entries/{id}/void`                    | a linked reversal (see "Fixing mistakes")                                  |

Every submit carries a client-minted **idempotency key** (`sourceRef`), so a double-click or retry maps
to "already posted" rather than posting twice. `GET /tenants/{id}/ledger.csv` exports the on-screen
ledger.

**The over-application rule (ADR-011).** A payment that exceeds what the tenant owes auto-splits the
excess into a prepayment. An _application_ has no such overflow, so applying a deposit **against charges**
— or applying a prepayment — that would exceed the tenant's open receivable is **rejected**
(`insufficient_receivable`); the composer asks the user to lower the amount. Applying a deposit **to owner
income** (damages) is deliberately _not_ capped — damages legitimately exceed any rent owed. This sits
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
_projection_ of the journal — nothing is independently maintained.

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

## Statements & reporting (M5)

An **owner statement** is a period summary that shows a property manager's fiduciary story to an
owner: beginning balance, income received, operating expenses, any applied deposit or credit events,
owner contributions, disbursements, and the resulting ending balance. Like every other view in
LeaseBook, it is a **query over the double-entry journal** — there is no separately maintained
statement ledger that could drift. The statement is generated on demand and the same journal replay
that computes it also verifies it independently.

### How the engine categorizes owner-equity lines into sections

The journal holds every financial event as lines tagged with an `event_type`. When a statement is
generated, only lines carrying `account_class = 'owner_equity'` for the requested owner are
considered — this is what structurally excludes PM income (`pm_income` lines can never match). Those
lines are grouped by `event_type` using a single exhaustive map (`StatementSectionMap`):

| Event type(s)                                         | Statement section                                              |
| ----------------------------------------------------- | -------------------------------------------------------------- |
| `RentCharged`, `FeeCharged`, `PaymentReceived`        | Income — rent collected                                        |
| `ManagementFeeAssessed`, `VendorPaid`                 | Operating expenses                                             |
| `DepositApplied`, `PrepaymentApplied`, `CreditIssued` | Applied deposits & credits                                     |
| `OwnerContribution`                                   | Owner contributions                                            |
| `OwnerDisbursed`                                      | Owner disbursement                                             |
| `BalanceForward`                                      | Folded into beginning balance only; never an in-period section |

The map is _exhaustive by construction_: any event that posts to `owner_equity` but has no entry in
this map throws at runtime rather than silently dropping a line off the statement. Adding a new
posting template that touches owner equity requires updating the map, and the property-based test
suite will catch the omission before deployment.

### The structural tie-out ($0.00 variance or issuance is blocked)

The statement engine runs **three independent reads over the journal** per owner per request:

1. **In-period lines** — the `owner_equity` movements that feed the sections.
2. **Beginning balance** — cumulative owner equity before `entry_date < period_start`.
3. **Independent period-end balance** — cumulative owner equity before `entry_date < period_end`
   (the same filter as the beginning balance but extended by one month, re-queried fresh from the
   journal — it is _not_ derived by adding the section totals).

The tie-out then computes `variance = statement_ending − journal_ending_balance`. If variance is
non-zero, the statement was not issued — the API returns HTTP 409 and the UI surfaces a fiduciary
warning. A zero variance means the statement's own arithmetic (begin + section totals) agrees with
an independent replay of the journal to the cent, ruling out any categorization or sign error in the
C# section pipeline.

This design is deliberate: the three-query structure means a bug in the grouping or sign convention
produces a _detectable_ non-zero variance, not a silently balanced ledger (which would happen if the
ending balance were derived from the section totals themselves).

### The fiduciary panel

Every rendered owner statement carries a **fiduciary integrity panel** — three computed assertions,
not static copy:

| Check                                    | What it proves                                                                                                                                                                                                                                   |
| ---------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **PM income excluded ✓**                 | The query is structurally scoped to `owner_equity` lines; no `pm_income` account-class line can ever enter the owner's section totals. This is a data-model constraint, not a filter applied at reporting time.                                  |
| **Deposits recognized on application ✓** | A `DepositApplied` or `PrepaymentApplied` entry appears in the _Applied deposits & credits_ section if and only if one was posted in the period. Collected-but-unapplied deposits sit in `deposit_liability` and never reach the owner's income. |
| **$0.00 variance ✓**                     | The structural tie-out (above) confirmed that the statement's section arithmetic matches an independent journal re-query to the cent for this period.                                                                                            |

If the variance check fails, the statement is still shown (for diagnosis) but marked unbalanced, the
panel flag is set, and delivery is blocked until the underlying journal discrepancy is resolved.

### The ADR-016 read-layer boundary

The statement engine is the one place in LeaseBook where the read layer is intentionally allowed to
span module boundaries (the ADR-007 cross-module exception). The rule is documented in ADR-016:

- **Accounting owns all financial math.** The `GetOwnerStatementData` handler runs inside the
  Accounting module, reading only `journal_lines` and `journal_entries` — no other module's tables.
  The section categorization, the per-basis filter, the PM-exclusion, and the tie-out all live here.
- **A batch port (`IOwnerStatementData`) carries results to the host.** The host's
  `StatementAssembler` calls this port for every requested owner in one batch, then enriches each
  result with display names (from Directory) and branding/reconciliation metadata (from Banking).
  None of the enrichment touches financial figures.
- **Reporting composes and presents; it never re-derives money.** If a report needs a financial
  figure that Accounting doesn't already expose, the fix is to extend the Accounting port — not to
  write a cross-module SQL join inside Reporting.

The report catalog (`/reports`) follows the same pattern: each report preview is dispatched
through `ISender` to the appropriate Accounting or Directory handler; the host's
`ReportPreviewService` aggregates the generic row payload the SPA renders. CSV and (for statements)
PDF exports use the same data path — no separate re-query for a different output format.

## Bulk operations (M6)

A property manager's month is batch-shaped: charge all tenants' rent on the first, apply late fees
to delinquent ledgers mid-month, then sweep owner equity and management fees out at month-end. M6
implements these three **bulk runs** as a shared pipeline — preview → confirm → atomic post → run
history — so the fiduciary concerns (idempotency, atomicity, audit, run-history) are written once
and inherited by all three.

### The three runs

**Monthly rent charge run.** Charges rent for every active lease in the chosen period. Active leases
whose `StartDate` or `EndDate` falls mid-period are **prorated** by actual days occupied (ADR-017):
daily rate = monthly rent ÷ actual days in month; charge = daily rate × days the lease is active in
the period; half-up rounding to the cent. The run is idempotent: re-running a period marks
already-charged leases as "already done" and posts only newly eligible ones — never double-charges.

**Late-fee run.** Selectively charges `FeeCharged(FeeKind.Late)` on delinquent ledgers past the
grace period. The effective policy is resolved per lease: `lease override ?? org default`, then
clamped to the **NC G.S. §42-46 statutory ceiling** (the late fee may not exceed the greater of
$15.00 or 5% of the monthly rent, regardless of the policy configured). Operators review the preview
and pick which delinquent ledgers to charge before confirming; the run is never silent.

**Owner disbursement run (with folded management fee).** For each owner, posts two events
atomically: `ManagementFeeAssessed` (equity × effective bps, half-up — ADR-018) followed by
`OwnerDisbursed` (net equity after fee, subject to the configured reserve floor). The management fee
is assessed on **owner equity available at run time** on the cash basis — not on rent collected —
so it reflects real distributable cash, not accruals. If the net after fee would fall below the
owner's `ReserveAmount`, the owner is excluded with a stated reason and the posting is not
attempted. Per-owner property overrides are deferred to a later milestone; M6 uses the
owner-level `DefaultMgmtFeeBps` only (documented in ADR-018).

### Source-ref idempotency convention (ADR-019)

Every posting made by a bulk run carries a deterministic `source_ref` key that ties the journal
entry to the run target and period:

```
{runType}:{year}-{month:00}:{targetKind}={targetId}
```

Examples: `rent:2026-05:lease=<leaseId>`, `disbursement:2026-05:owner=<ownerId>`.

The `source_ref` is checked by the existing `(org_id, source_ref)` partial unique index on
`journal_entries` before and after posting. A second run for the same target and period raises
`DuplicateSourceRefException`, which the run engine catches per-item and records as `Skipped` (not
an error). This guarantees no double-posting even under concurrent confirms.

### Cross-module boundary (ADR-019)

Operations reads preview inputs from Directory and Accounting through **consumer-owned ports +
host adapters**, exactly mirroring M5's read-direction ADR-016. The host adapter for writes
(`BatchPostingAdapter`) loops the existing public `IAccountingEvents.PostAsync` for each intent;
there is no new batch command in Accounting. Operations never references Accounting's event types —
the adapter translates Operations primitives (target id, amount, date, period, kind) into the
correct Accounting events (`RentCharged`, `FeeCharged`, `ManagementFeeAssessed`, `OwnerDisbursed`).

### Run history

Every confirmed run writes an auditable `bulk_runs` row (who ran, period, run type, summary
counts) and one `bulk_run_items` row per target (status: `Posted` / `Skipped` / `Excluded`,
snapshot JSON with the amounts and entry id). The run history view lists all completed runs
for the organization, most recent first.

### Period locking

A locked accounting period (from a finalized bank reconciliation) surfaces as `Excluded` items in
the run preview and confirm, not as a run-level failure. The run posts what it can and records the
locked-period targets as excluded with a reason, so a partially locked month is handled gracefully.

See also: **ADR-017** (rent proration method), **ADR-018** (management-fee rounding), **ADR-019**
(bulk-run engine and cross-module batch posting).

## Migration / balance-forward cutover (M7)

A new PM cuts over from AppFolio (or another system) by importing the **closing positions** from
their last month-end in the old system. LeaseBook starts clean from that boundary — no fake history,
no re-keying. Historical ledgers stay in AppFolio; everything before the cutover date is simply not
in LeaseBook.

### The clearing account is the tie-out

Each imported position — owner equity, security deposit held, trust bank book balance, tenant
receivable — posts as **one self-balancing journal entry**: one leg on the real account and an
equal-and-opposite leg on a transient `MigrationClearing` account. The clearing account is what
makes a non-tying import a detectable discrepancy rather than a posting failure or a silent error.

After all rows are imported:

- If the import is internally consistent — bank balances equal owner equity plus deposit liabilities
  (the trust equation) — then every clearing debit is offset by an equal clearing credit, and the
  **`MigrationClearing` balance nets to exactly $0.00** in both the cash and accrual bases.
- If the import does not tie, the residual in `MigrationClearing` is the exact dollar amount of the
  discrepancy, shown per-basis and per-account in the verification report.

Go-live is blocked until the verification report shows $0.00 in both bases and the PM clicks
**Approve**. That approval is recorded as an immutable, audited snapshot — the migration analogue
of the M4 reconcile-to-$0 finalize and the M5 statement tie-out.

### Deposits are liabilities, not income

Imported deposit liabilities post to `security_deposits_held` — a liability account — exactly as a
live deposit collection does. They do not hit income. This is the same rule that governs deposits
collected through the normal workflow: the money belongs to the tenant until it is applied at
move-out. Importing from AppFolio does not change the classification.

### Opening entries, not fake history

The engine posts one journal entry per imported row, dated at the cutover boundary. There are no
synthetic per-month historical entries, no reconstructed ledger activity. Every owner statement,
tenant ledger, and bank register for dates after the cutover is a projection of real LeaseBook
activity, starting from these opening positions. Dates before the cutover are in AppFolio.

### Idempotency and re-import

Each opening entry carries a deterministic `source_ref`
(`opening:{cutover}:{type}={subledgerId}`), keyed to the same `(org_id, source_ref)` partial
unique index that bulk runs use. Re-uploading the same CSV is a no-op. The `source_ref` is
**figure-blind** — it identifies the subledger position, not the amount — so re-importing a balance
with a **changed figure does NOT overwrite** the already-posted opening entry (the duplicate
`source_ref` is detected and the row is recorded as already-posted; the corrected figure never
posts). To correct an already-posted opening figure **before sign-off**, re-provision the cutover
org and re-import. An in-product supersede/correction workflow is **deferred to M8**.

> **Not imported in M7:** only owner / deposit / bank / receivable balance kinds are imported. The
> **held-PM-fees opening position** (ADR-020 §5) is **not** imported — it would touch `pm_income`,
> which M7 deliberately keeps out of the import path. Any held-fee opening position surfaces as a
> migration-clearing residual the operator reconciles before sign-off.

See also: **ADR-020** (opening-balance posting model and clearing-account design),
**ADR-021** (migration toolkit architecture, verification gate, and AppFolio parser seam).
