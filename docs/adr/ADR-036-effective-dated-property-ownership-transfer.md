# ADR-036: Effective-dated property ownership transfers preserve deposit responsibility

- **Status:** Accepted
- **Date:** 2026-08-13
- **Deciders:** Jerry Holland

## Context

`Property.OwnerId` was mutable directory data, while journal owner dimensions are immutable
historical attribution. A sale could therefore make new events use the buyer without recording when
the handoff occurred, and a held security deposit remained credited to the seller's liability bucket.
ADR-026 deliberately made that mismatch fail invariant I7 and required an explicit transfer template
rather than a relaxed invariant.

## Decision

Property ownership changes use one effective-dated command, never the ordinary property-edit command.
It appends a `property_ownership_transfers` transition, updates the current Directory owner, and calls
Accounting through a Directory-owned host port in the same transaction.

Accounting posts `DepositResponsibilityTransferred` on the effective date. For every positive security
deposit position held by the seller for the property on that date, it debits the seller's exact
tenant/bank liability bucket and credits the buyer's bucket. It moves neither bank cash nor owner
equity; sale settlement is separate. The accounting-period and affected-bank reconciliation locks
apply, and exact-bucket disposition guards prevent either owner from consuming the other's position.

New tenant-account events resolve the property owner effective on their accounting date. A shared
property-row lock serializes that resolution with a transfer. Existing journal lines are never
rewritten, even when a transfer is recorded with an earlier effective date. Ownership transitions are
strictly ordered per property, org-scoped by RLS, and append-only for the runtime role. A transfer may
be effective today or earlier, but not in the future: the current-owner projection changes when the
command commits, and scheduled closings require a separate state model.

A backdated transfer is rejected when a non-zero seller-attributed deposit change already exists
after the proposed effective date. Moving only the effective-date position would strand that later
change under the former owner; including it in the earlier entry would manufacture a liability before
the underlying deposit activity. The operator must reverse and re-post the later activity first.

## Consequences

The Directory owner, ownership history, deposit responsibility and transfer journal entry commit or
roll back together. I7 remains strict, owner-facing held-deposit balances hand off without changing
the trust equation, and later posts can reconstruct the correct owner for their accounting date.

Backdated ownership records do not restate already-posted activity; correcting a posting still uses a
linked reversal and re-post. The current owner remains denormalized on `properties` for ordinary
Directory reads, so every ownership change must go through the transfer command.

## Revisit trigger

Revisit when a sale workflow must also settle seller owner equity, schedule future closings, or model
partial/co-ownership. Each requires an explicit settlement or ownership model rather than adding
lines to the deposit-responsibility transfer.
