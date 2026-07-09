# Milestone 1 — Retrospective

**Status:** all 8 work packages complete and committed on `m1/accounting-core` (off `main`). Automated
Integration Gate (§D) green. Executed **inline** (single implementer, wave order) rather than via
subagents — the orchestrator/subagent framing in the plan was a delivery mechanism; the deliverables
and pins were honored exactly. Two gate items remain operator-only: CI-green-on-GitHub → merge to
`main` (the agent commits but never pushes/merges), and the local gitleaks scan (not on PATH; runs in
CI, no new secrets introduced — the dev password is already allowlisted).

## Gate evidence (§D)

| Step | Result |
| --- | --- |
| 1. `reset-db` → healthy; migrate from blank | ✅ InitialOrgs + AddAuditEvents + AddIdentity + **AddAccountingCore** |
| 2. `seed --org demo` ×2 idempotent | ✅ 29 entries / 91 lines / 8 accounts after two runs (journal step skips) |
| 3. `check-invariants --org demo` | ✅ exit 0 ("all clean across 1 org") |
| 4. `dotnet build` + full `dotnet test` | ✅ **101 tests** — SharedKernel 26, Architecture 6, Integration 18, Accounting 51; build 0/0 |
| 5. web `lint/typecheck/test/build` | ✅ lint 0, typecheck 0, test 24, build OK (regenerated `schema.d.ts`) |
| 6. API smoke (login → owners/balances, trust-equation, diagnostics) | ✅ o1 operating 14,820.50; oper trust variance 0.00 (book 248,930.14 = equity 248,855.14 + prepay 75.00); diagnostics 404 |
| 7. `docker build` → SPA + `/api` | ✅ image builds; serving unchanged from M0 |
| 8. format clean; gitleaks | ✅ format clean; gitleaks ⏳ CI (no new secrets) |
| 9. ADR-006 + `docs/accounting.md` + runbook + CLAUDE.md | ✅ all present; `check-invariants` added to Commands |
| 10. TODO §M1 checked + Status Ledger + retro | ✅ this document |
| 11. CI green → merge to main | ⏳ operator (push + GitHub Actions + merge; branch protection) |

Totals: **125 automated tests green** (101 .NET + 24 web), build 0/0, format clean, container builds,
API smoke reproduces the golden figures over HTTP.

## Engine facts proven

- The Tarheel demo replays through the real engine and reconciles to the cent: Σ bank books
  **483,620.69** (= `kpis.trustTotal`), the focal tenant's 12 ledger rows, tenant balances t1–t7,
  owner operating + deposit balances o1–o8, and **trust-equation variance 0.00** on both trust banks.
- The §C.8 BalanceForward residuals tie by construction (oper BF book 246,075.14 = Σ oper owner-equity
  BF; final oper 248,930.14 after replay), verified by `check-invariants` and the golden tests.

## Deviations from the plan (and why)

- **`SqlQuery<unmapped record>` needs snake_case column aliases** (WP-06): the host uses
  `UseSnakeCaseNamingConvention`, which rewrites the *expected* column names for unmapped result types
  too. SELECT aliases are snake_case (`bank_account_id`, `owner_equity`), not the PascalCase property
  names. Caught immediately by a probe; all read-model SQL follows this.
- **`Money?` columns get an explicit converter in the line config** (pitfall M-E6): the host's
  `Properties<Money>()` convention was not relied upon for the nullable debit/credit columns; the
  converter + `numeric(14,2)` are pinned in `JournalLineConfiguration`, and the WP-01 round-trip test
  proves exactness.
- **`LeaseBook.Tests.Common` references `xunit.v3.extensibility.core`, not `xunit.v3`** (WP-02): the
  metapackage requires `<OutputType>Exe</OutputType>`; the shared fixture library only needs the
  `IAsyncLifetime`/`ICollectionFixture` abstractions.
- **FORCE RLS applies to the migrator** (WP-07 injected-violation test): planting deliberately bad data
  as the table owner still needs `app.org_id` set for the WITH CHECK policy. The test sets it.
- **Period get-or-create + source_ref/reversal idempotency rely on EF's automatic SaveChanges
  savepoint** inside the ambient transaction: a unique-violation rolls back to the savepoint, leaving
  the transaction alive to catch-and-reread (P32, the duplicate/already-reversed mapping). This is the
  mechanism that makes the lazy period race tolerable without a second connection.
- **Tenant ledger excludes security deposits** (code ∈ {tenant_receivable, tenant_prepayments}) so the
  rent ledger nets correctly and the focal tenant is exactly 12 rows (the cutover deposit is a
  BalanceForward line, not a ledger transaction). Consequence: the §C.6 `DepositCollected → "Security
  Deposit"` category never renders in the M1 tenant ledger (deposits live in the register). The
  category derivation is implemented in full regardless.
- **`check-invariants` CLI verb** ships per P33 instead of a scheduled job; ADR-006 records the Hangfire
  timing call.

## §C.9 oddities — dispositions carried forward (unchanged, recorded for the owning milestone)

All seven §C.9 dispositions stand as written. M1 seeded per the *ledger* dating and used the
auto-split (Mercer 2,225) deliberately. The February focal running balances were treated as authoring
noise and asserted against the engine's values (#1); the goodwill credit transiently drives Jasmine's
receivable to −85 then back to 0 (allowed — credits are unguarded).

## What M2 planning must absorb

- **DemoIds is the directory handoff.** M2 adds Owner/Property/Unit/Tenant tables reusing the exact
  `DemoIds` GUIDs the journal dimensions already carry (P26), then adds the FKs by migration. The
  synthetic aggregates (`AggregateOwners`, `AggDepO1..O8`, `AggregateDepositsUnattributed`) stand in
  for the 15 unlisted owners / unattributed deposit liability — M2 decides whether to materialize the
  real 23 owners or keep the aggregates.
- **`owners/balances` is the dashboard-hero shape** (`{ ownerId, operating, deposits, total }`,
  cash-basis). M2 consumes it at 0 clicks. It currently also returns the `AggregateOwners` row — M2
  filters/relabels it.
- **ownersPayable (§C.9 #6):** the JSON `132,447.00` matches no derivable combination of the owner
  figures. M2's dashboard defines "owners payable" computationally and decides whether the KPI figure
  or the computed value wins.
- **Deferred to later milestones, per §C.6/§C.9:** bank cleared/uncleared columns and `bankTxns`
  register fidelity (M4 reconciliation); the statement block + the Okonkwo/Liu statement tenants
  (already in `DemoIds`, unused) (M5); fee-percentage computation and a rounding ADR (M6).
- **The error map (§C.5) is wired in the host** (`AccountingExceptionHandler`) but unexercised over
  HTTP in M1 (no write endpoints) — M3's composer is the first real producer.
- **Identity-table soft spot untouched** (retro item 3) — no user-management endpoints in M1, as
  scoped.

## Post-gate review findings (advisory — recorded in TODO.md at the owning milestone)

The pre-merge review of `m1/accounting-core` confirmed every invariant holds but flagged two
caller-trust gaps in the event catalog. Both are consistent with M1's "amounts taken as given"
scope (P28) and break no written invariant; both must be closed by the first real write surface
that produces the event:

1. **`DepositApplied → AgainstCharges` is unguarded against the open receivable** (owned by **M3**,
   noted on the deposit-handling bullet). The guard checks the held deposit (I4) but a deposit
   applied "against charges" larger than what the tenant owes drives the receivable negative —
   unlike `PaymentReceived`, whose auto-split routes excess to prepayments. The M3 composer must
   clamp/warn/split; until then only the seeder and tests post this event, with valid amounts.
2. **`RefundIssued` accepts the refund bank from the caller** (owned by **Phase 3**'s deposit
   disposition wizard, noted there). The liability and bank lines share the supplied bank tag, so
   I2 balances per bank even if a deposit refund is tagged to the operating trust — i.e. the
   equation cannot catch a wrong-bank refund. The wizard must derive the bank from the liability's
   source rather than take it as input.
