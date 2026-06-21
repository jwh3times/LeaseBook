# ADR-015: CSV bank-statement import, auto-match, and dedup

- **Status:** Accepted
- **Date:** 2026-06-21
- **Deciders:** Engineering

## Context

M4 WP-05 stands up `Modules.Banking` — the first time that module has real types — to make month-end
reconciliation stop being manual ticking: import a bank statement, auto-match its lines against the
uncleared register, clear the matches, and skip anything already imported. The decisions below touch a
module boundary (Banking must reach Accounting only through ports, ADR-007), the money-precision
invariant, and the append-only/RLS posture every M4 table inherits.

## Decisions

### 1. CSV only, behind a parser seam; OFX/QFX deferred (P66)

The importer parses **CSV** through an `IStatementParser` interface (`Parse(content, ColumnMap)`), with
`CsvStatementParser` (CsvHelper — already in the stack for the M3 ledger export, no new package) as the
only implementation this milestone. OFX/QFX is a later add: a second implementation behind the same
interface, no change to the import pipeline. A **column map** (`Date`, `Description`, plus either a
signed `Amount` column or a `Debit`/`Credit` pair, optional `DateFormat`) adapts to each bank's layout;
named maps are saved per bank account (`bank_csv_mappings`) so the operator picks one next time.

### 2. Customer-account sign convention; precision gated at the parse boundary

A statement line's `Amount` is **signed**: a deposit is `+`, a withdrawal is `−`. With a `Debit`/`Credit`
pair the parser computes `credit − debit` (a credit increases the customer's balance → `+`). This is the
same sign as a `RegisterCandidate` (`Deposit ?? −Withdrawal`), so the matcher compares like with like.
Money is `NUMERIC(14,2)`; a parsed amount with a third significant decimal is **rejected at the parse
boundary** as a row error rather than silently rounded downstream (P28). Parsing is **row-tolerant** — a
bad date/amount is collected as a `RowError` (1-based data-row index + reason) and reported, never fatal.

### 3. The CSV arrives as text in a JSON body, not multipart

The import endpoint takes the CSV **as text** in a JSON body (`{ filename, csvContent, columnMap }`), not
a multipart file upload. The SPA reads the file client-side (it already needs the parsed header row to
offer column mapping), so the server stays stateless about file storage, and the request maps cleanly to
the OpenAPI/TS-client + MSW test path. Bank-statement CSVs are kilobytes; size is a non-issue. (Plan
§B.6 left transport unspecified; this records the choice.)

### 4. Dedup is a content hash, unique per (org, bank account)

A re-imported statement must not double-count. Each line gets a `dedup_hash` =
`SHA-256(statement_date | amount(2dp) | normalized_description)`, where normalization folds case and
whitespace runs (banks vary these between exports) but date and amount are exact. `statement_lines`
carries `bank_account_id` (denormalized from its import) with `UNIQUE (org_id, bank_account_id,
dedup_hash)`, so dedup spans **all** imports for that account, not just one file. The importer skips
colliding lines (against prior imports *and* identical earlier rows in the same file) and reports the
count; the unique index is the database backstop. **Plan §B.6 listed the dedup unique on
`statement_lines` but omitted `bank_account_id` from its column list — the column is required for the
constraint to span imports, so it was added.**

### 5. Auto-match heuristic (P67)

`AutoMatcher` is a **pure, deterministic** function (unit-tested, no DB): for each statement line, against
the uncleared register candidates,

- exact amount **and** date within ±`N` days (`N = 4`) → `matched`;
- exact amount, date outside the window → `suggested`;
- no amount match → `unmatched`.

Two passes (in-window matches claimed first), choosing the closest candidate by date (ties by id), and a
candidate is **never claimed by two** statement lines. The register read that feeds it spans the
statement dates **plus a 45-day margin**, so an exact-amount candidate outside the ±4 match window is
still read and surfaced as `suggested`. Every confirmed decision is persisted to `statement_matches`
(`matched`/`suggested`/`unmatched`/`created`) for audit.

### 6. Banking reaches Accounting only through consumer-owned ports (ADR-007 / P68)

Banking declares **`IBankRegister`** (windowed batch read of uncleared register candidates) and
**`IBankClearing`** (apply clearances to a set of `journal_line_id`s) in `Banking.Contracts`. Host
**adapters** (`BankRegisterAdapter`, `BankClearingAdapter` in `src/LeaseBook.Web/Adapters/`) implement
them by dispatching Accounting's `GetBankRegister` (filtered to uncleared) and `ApplyClearances` via
`ISender`. Banking **never** reads `journal_lines` / `bank_line_status` or writes clearance state
directly — confirming a match clears through the port, which is the only reason a matched line shows
`cleared` in the register (`ModuleBoundaryTests` keeps the assembly boundary; the import HTTP test proves
the functional path). `journal_line_id` on `statement_matches` is a bare reference, no cross-module FK.

### 7. The four import tables are org-scoped through the RLS helper (P70)

`bank_csv_mappings`, `statement_imports`, `statement_lines`, `statement_matches` each get
`org_id NOT NULL` + the equality policy + `FORCE ROW LEVEL SECURITY` via `Rls.EnableOrgRls`, and the
`SchemaGuardTests` guard stays green. None is append-only (they are operational/config metadata), so the
runtime role keeps its INSERT/UPDATE grants. FKs to `bank_accounts` are composite
`(org_id, bank_account_id)` (P60), DB-only, no EF navigation (P26).

## Consequences

- A reconciliation can start from imported data: import → preview → confirm clears the matched lines
  through the same clearance path the register UI uses; a re-import is a no-op.
- The matcher is a pure function, cheap to test exhaustively and to tune (`N`, the read margin) later.
- Banking stays extractable: it owns no cross-module SQL and depends only on SharedKernel + its own
  Contracts.

## Revisit trigger

When OFX/QFX import is scheduled, add a parser implementation behind `IStatementParser` (no pipeline
change). If statement volumes ever make the text-body transport or the page-through register read costly,
revisit multipart upload and/or a dedicated uncleared-candidate read. If Phase-2 Stripe payout lines need
matching, they flow through the same `IBankRegister`/`AutoMatcher` path.
