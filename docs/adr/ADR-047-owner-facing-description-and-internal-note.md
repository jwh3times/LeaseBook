# ADR-047: A journal entry's description is owner-facing; staff notes are separate

- **Status:** Accepted
- **Date:** 2026-09-29
- **Deciders:** Jerry Holland

## Context

Every business event carries one free-text `Description`, stored on `journal_entries.description`.
Issued owner statements print it on every line, in the PDF and the CSV, and a void printed
`VOID: {reason}` in the section of the entry it reversed. Nothing told staff that owners read either
one. Worse, the staff field that fed it was called **Memo**, in the composer and in the request DTOs,
which reads as internal. Issued statements are immutable (ADR-040), and the owner portal now makes
them retrievable at any time, so a stray note becomes a permanent disclosure.

The journal is append-only: posted rows are never updated, the runtime role has no `UPDATE` grant on
journal tables, and corrections are linked reversals. `journal_lines.memo` already exists. It is
line-level, not used for display, and set only by opening-position and balance-forward lines.

## Decision

1. **Two fields, two audiences.** `journal_entries.description` stays and is **owner-facing**. A new
   nullable `journal_entries.internal_note` is **staff-only**. The note is written once, at posting,
   through `PostEntryRequest` → `JournalEntry.Create`, like every other column. It has no update path,
   the runtime role cannot `UPDATE` it, and it is not a correction mechanism. A blank note is stored
   as `NULL`. `journal_lines.memo` is unrelated and unchanged.
2. **Staff-typed writes take both.** Every staff-typed business event gains an optional
   `InternalNote`. The request DTOs rename `Memo` to `Description`, so nothing owner-facing is called
   a memo, and add `internalNote`. Those renamed requests reject unmapped JSON members, so a stale
   client still sending `memo` gets a coded 400 (`invalid_request`, ADR-025's 2026-09-29 amendment)
   rather than a post that silently drops its text. On `IssueCredit` and `ApplyDeposit`, `Reason` remains the
   owner-facing text, and each also accepts `internalNote`. System-generated events (rent, late-fee
   and disbursement runs, sweeps, deposit transfers, simulated payments, opening positions) carry no
   note.
3. **A void's reason is internal.** `VoidEntry` still requires a reason. The reason becomes the
   reversal's `internal_note`, and the reversal's owner-facing description is
   `Void — {original description}` (plain `Void` when the original had none). The owner sees that a
   correction happened, and to what, but not why. This applies to every reversal posted through
   `IReversalService`, including the import supersede path. It applies only to **new** voids: existing
   reversal rows keep their `VOID: {reason}` text, and issued statements are not re-rendered.
4. **Owner-facing reads never select `internal_note`.** That covers the owner-statement data and
   everything built from it (PDF, CSV and the issued artifact), the owner portal and the tenant
   portal. **Staff reads show it** beside the description: the tenant ledger and its CSV, the bank
   register (whose search matches the note too), the trust-ledger report, the compliance pack (where
   a void's reason is audit-relevant), and the staff statement view. The staff statement view gets
   its notes from a separate route, `GET /api/statements/{ownerId}/internal-notes`, keyed by entry
   id. `StatementView` is the record that is rendered and issued, so it carries no note field for one
   to leak through. Audit rows capture the note automatically through `SaveChanges`.

## Consequences

- Staff get a place for context the owner should not read, and the UI can label the owner-facing
  field for what it is.
- The guarantee is tested rather than asserted. `InternalNoteNoLeakTests` seeds notes and void reasons
  and scans the owner statement's PDF text, CSV and JSON on both bases, the issued artifacts, and both
  portals' JSON. Temporarily wiring the note into the statement query turns it red on every
  owner-statement surface. The portal suites pin their JSON property sets, and a schema test pins the
  missing `UPDATE` grant.
- History mixes two void formats: reversals before this change read `VOID: {reason}` and have no note,
  later ones read `Void — {original}`. That is the price of immutability. No backfill rewrites a
  posted row or an issued statement.
- A note cannot be edited or retracted. A note typed in error stays on the entry, visible to staff
  only. The fix for a wrong **entry** remains a void.
- A statement void line no longer explains itself to the owner. Whether owner statements must state
  why a correction was made is a separate compliance question. This decision keeps the void visible
  and names what it corrected.

## Revisit trigger

Reopen this if the compliance review concludes that owner statements must state the reason for a
correction, or if staff need to amend or retract notes. Amending would need an append-only note
history, not an `UPDATE` grant on the journal. Also reopen it if notes are needed per line rather than
per entry.
