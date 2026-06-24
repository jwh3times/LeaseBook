# AppFolio Export Catalog — Migration Research Spike

> **Status: Framework documented — column headers are best-guess candidates pending validation.**
> See the [Gate section](#gate-validate-with-the-beta-customers-real-exports) below for what is
> unverified and how to update it once real exports are in hand.

This document maps each LeaseBook `EntityKind` to the AppFolio report/export an operator pulls,
lists the canonical fields the importer expects, and shows the current best-guess candidate column
headers in `AppFolioProfiles`. The wizard's per-column remap step is the fallback for any header
the auto-matcher does not recognize.

---

## Import phases and dependency order

**Phase 1 — Entities** (must complete before balances; within phase, order matters):

1. Owners
2. Properties (reference owners)
3. Units (reference properties)
4. Tenants & Leases (reference units)

**Phase 2 — Opening balances** (all reference entities from Phase 1; cutover date must match across
all four kinds):

5. Owner balances
6. Deposit liabilities
7. Bank balances
8. Tenant receivables

The balance-forward / cutover model: each balance file captures the state of the subledger at
close-of-business on the cutover date. LeaseBook posts a single opening journal entry per row
(real account vs the `MigrationClearing` contra) dated at the cutover boundary. The clearing
account nets to $0 when the import is correct; any residual is the quantified discrepancy.
See ADR-020 (opening-balance posting) and ADR-021 (migration toolkit architecture) for the
engine design.

---

## Entity kinds

### `Owners`

**AppFolio source:** *Owner directory* — typically exported from Reports > Owner > Owner List, or
from the Contacts / Owners section as a CSV export.

| Canonical field | Required | Candidate headers (best-guess — see Gate) |
|---|---|---|
| `external_id` | yes | `Owner ID`, `ID` |
| `name` | yes | `Owner Name`, `Name` |
| `reserve` | no | `Reserve`, `Reserve Amount` |

**Typed row:** `OwnerRow(ExternalId, Name, Reserve)`

---

### `Properties`

**AppFolio source:** *Property list* — Reports > Property > Property List, or the Properties
section CSV export.

| Canonical field | Required | Candidate headers (best-guess — see Gate) |
|---|---|---|
| `external_id` | yes | `Property ID`, `ID` |
| `external_owner_id` | yes | `Owner ID` |
| `address` | yes | `Address`, `Property Address` |

**Typed row:** `PropertyRow(ExternalId, ExternalOwnerId, Address)`

---

### `Units`

**AppFolio source:** *Unit list* — typically included in property exports or a separate Units
export from the Properties section.

| Canonical field | Required | Candidate headers (best-guess — see Gate) |
|---|---|---|
| `external_id` | yes | `Unit ID`, `ID` |
| `external_property_id` | yes | `Property ID` |
| `label` | yes | `Unit`, `Unit Name`, `Label` |
| `rent` | no | `Market Rent`, `Rent` |
| `status` | no | `Status` |

**Typed row:** `UnitRow(ExternalId, ExternalPropertyId, Label, Rent, Status)`

---

### `TenantsLeases`

**AppFolio source:** *Tenant / Lease directory* — Reports > Tenant > Tenant List, or the Tenants
section CSV export. AppFolio combines tenant contact info and lease terms in one export.

| Canonical field | Required | Candidate headers (best-guess — see Gate) |
|---|---|---|
| `external_id` | yes | `Tenant ID`, `Lease ID`, `ID` |
| `external_unit_id` | yes | `Unit ID` |
| `name` | yes | `Tenant Name`, `Name` |
| `start` | no | `Lease Start`, `Start` |
| `end` | no | `Lease End`, `End` |
| `rent` | no | `Rent` |
| `deposit` | no | `Deposit`, `Deposit Required` |
| `status` | no | `Status` |

**Typed row:** `TenantLeaseRow(ExternalId, ExternalUnitId, DisplayName, StartDate, EndDate, Rent, DepositRequired, Status)`

---

### `OwnerBalances`

**AppFolio source:** *Owner ledger / equity report* — Reports > Owner > Owner Statement or Owner
Balance summary as of the cutover date. One row per owner; cash and accrual bases exported
separately or as two columns.

| Canonical field | Required | Candidate headers (best-guess — see Gate) |
|---|---|---|
| `external_owner_id` | yes | `Owner ID`, `Owner Id`, `ID` |
| `name` | yes | `Owner Name`, `Name` |
| `cash_balance` | yes | `Cash Balance`, `Cash` |
| `accrual_balance` | yes | `Accrual Balance`, `Accrual` |

**Typed row:** `OwnerBalanceRow(ExternalOwnerId, Name, CashBalance, AccrualBalance)`

---

### `DepositLiabilities`

**AppFolio source:** *Security deposit register* — Reports > Property > Security Deposit Liability
or the Deposit Held report as of the cutover date. One row per tenant.

| Canonical field | Required | Candidate headers (best-guess — see Gate) |
|---|---|---|
| `external_tenant_id` | yes | `Tenant ID`, `Tenant Id` |
| `external_owner_id` | yes | `Owner ID`, `Owner Id` |
| `held_amount` | yes | `Deposit Held`, `Held`, `Amount` |

**Typed row:** `DepositLiabilityRow(ExternalTenantId, ExternalOwnerId, HeldAmount)`

---

### `BankBalances`

**AppFolio source:** *Bank account register* — Reports > Banking > Bank Account Detail or the
Bank Book Balance report as of the cutover date. One row per trust/deposit/operating account.

| Canonical field | Required | Candidate headers (best-guess — see Gate) |
|---|---|---|
| `external_bank_id` | yes | `Account ID`, `Bank Account`, `Account` |
| `name` | yes | `Account Name`, `Name` |
| `book_balance` | yes | `Book Balance`, `Balance` |

**Typed row:** `BankBalanceRow(ExternalBankId, Name, BookBalance)`

---

### `TenantReceivables`

**AppFolio source:** *Accounts receivable / tenant balance report* — Reports > Tenant > Tenant
Balance or Aged Receivables as of the cutover date. One row per tenant with an outstanding balance.

| Canonical field | Required | Candidate headers (best-guess — see Gate) |
|---|---|---|
| `external_tenant_id` | yes | `Tenant ID`, `Tenant Id` |
| `external_owner_id` | yes | `Owner ID`, `Owner Id` |
| `balance` | yes | `Balance Due`, `Receivable`, `Balance` |

**Typed row:** `TenantReceivableRow(ExternalTenantId, ExternalOwnerId, Balance)`

---

## Gate: validate with the beta customer's real exports

> **All AppFolio column header names in the tables above are best-guess candidates.** They have
> NOT been validated against a real AppFolio export. Until the beta customer provides actual
> export files, treat every header name in this document — and in `AppFolioProfiles.For(...)` —
> as an unverified approximation.

### What is unverified

- The exact column header strings AppFolio writes in each export (casing, spacing, special
  characters, presence of extra columns).
- Which specific AppFolio report menu path produces the most reliable export for each entity kind.
- Whether AppFolio produces a single combined export or separate files for e.g. tenants vs leases,
  cash vs accrual owner balances.
- Whether some columns are absent in older AppFolio plan tiers.

### What to do when real exports are in hand

1. Open the beta customer's CSV files and note the exact header row for each export.
2. Compare each header against the `candidateHeaders` array in
   `src/LeaseBook.Migrator/Profiles/AppFolioProfiles.cs` for that `EntityKind`.
3. Add any real header that is missing from the candidate list. Remove guesses that are wrong.
   **This is a data edit, not a code change** — `AppFolioProfiles.For(kind)` is a switch returning
   `ColumnMappingProfile` records; update the string arrays in place.
4. Update the tables in this document to reflect confirmed vs. still-guessed headers.
5. For headers the auto-matcher still misses, the wizard's per-column remap step lets the operator
   map unrecognized columns interactively — no code change required for one-off mismatches.
6. Once all headers for an entity kind are confirmed, remove the "best-guess" caveats for that kind.

### Example edit (illustrative — do not treat as confirmed)

```csharp
// Before (best-guess):
new("external_id", ["Owner ID", "ID"], Required: true),

// After (confirmed from real export):
new("external_id", ["Owner ID", "OwnerID"], Required: true),
```

Until this gate is cleared, the wizard's column-remap step is the safety net. The importer is
intentionally tolerant: unrecognized headers are surfaced as warnings, not hard errors, and the
operator can remap them before committing the import.
