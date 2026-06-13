# ADR-010: Ledger write commands wrap the engine; the actor is attributed at the seam

- **Status:** Accepted
- **Date:** 2026-06-13
- **Deciders:** Engineering

## Context

M3 turns the read-only tenant page into the action hub where money first moves through the UI. The M1
engine already posts every business event M3 needs (`PaymentReceived`, `FeeCharged`, `RentCharged`,
`DepositCollected`, `PrepaymentReceived`, `DepositApplied`, `PrepaymentApplied`, `CreditIssued`, and
the reversal path) through a single write path — the posting service — which is the only thing that
constructs `JournalEntry`/`JournalLine` rows and the only place the append-only and per-basis-balance
invariants are enforced (ADR-006, CLAUDE.md). M3 needs an HTTP write surface the SPA can call, and it
needs to finally record *who* posted each entry — the `created_by` / `actor_user_id` columns have
existed since M0/M1 but were always stamped null.

Two anti-patterns to avoid:

1. **A second write path** — an endpoint or command that builds journal lines itself. That would
   duplicate the balancing/append-only/period rules outside the one module that owns them, exactly the
   correctness hazard the single-write-path rule exists to prevent.
2. **Trusting the client for posting dimensions** — letting the SPA send `owner_id`/`property_id` on a
   post. A wrong or stale value silently mis-attributes money to the wrong owner.

## Decision

**The M3 write surface is a thin command layer over the existing engine, not a new write path.**

- One CQRS command per composer action (`RecordPayment`, `AddCharge`, `IssueCredit`, `CollectDeposit`,
  `CollectPrepayment`, `ApplyDeposit`, `ApplyPrepayment`, `VoidEntry`) lives in
  `Modules.Accounting/Features/LedgerPosting`. Each has a FluentValidation validator and a handler that
  (a) resolves the tenant's `(ownerId, propertyId, unitId)`, (b) constructs the matching business-event
  record, and (c) dispatches `IAccountingEvents.PostAsync` / `IReversalService.ReverseAsync`. No command
  constructs a `JournalEntry`/`JournalLine`; the posting service stays the sole write path.
- **Posting dimensions are resolved server-side** from the tenant's active lease through a
  consumer-owned port `Accounting.Contracts.ITenantPostingDimensions` + a host adapter that delegates to
  a Directory query (ADR-007). The request body carries only the tenant id + amount/date/method/bank. A
  tenant with no active lease is rejected (a validation failure), never defaulted.
- Endpoints are minimal-API, `RequirePMStaff` (money entry is staff-level; only *settings* writes are
  admin), thin (bind → dispatch → `TypedResults`), and let the M1 `AccountingExceptionHandler` map
  domain rejections to 422/409. Each submit carries a client-minted `sourceRef` idempotency key, so a
  double-submit maps to `duplicate_source_ref` rather than double-posting; void supplies its own key via
  a non-breaking `IReversalService.ReverseAsync` overload.
- **Actor attribution is a request-scoped seam.** A host `IActorContext` (a bare `Guid? UserId`, kept in
  `SharedKernel.Tenancy` so the kernel stays pure) is populated by the org-context middleware from the
  authenticated user-id claim, alongside the existing org resolution. `AppDbContext`'s audit pass stamps
  `audit_events.actor_user_id` and `PostingService` stamps `journal_entries.created_by` from it; both
  are optional constructor dependencies, so the seeder, background jobs, and the test harness keep
  writing as the system (a null actor) without throwing. The per-entry audit trail
  (`GET /entries/{id}/audit`) resolves an actor id to a name/email via an explicit org-filtered
  `asp_net_users` lookup (the identity table carries no RLS — the org filter is the boundary).

M3 ships **no schema migration**: `created_by`/`actor_user_id` already exist and no new entities are
added.

## Consequences

- The journal's correctness rules stay in one place; the UI cannot post an unbalanced or
  mis-dimensioned entry, because every post still flows through the engine and the dimensions come from
  the lease, not the client.
- Attribution is now live for every post and reversal made by an authenticated user, feeding the audit
  drawer and (later) the compliance pack — at the cost of one request-scoped context and two one-line
  stamp sites.
- The `AccountingExceptionHandler`, wired since M1, gets its first HTTP producer; its 422/409 mapping is
  now asserted over the wire.

## Revisit trigger

When the org-aware composite `(org_id, id)` FK rework lands (M4) the posting path and harness reopen;
re-confirm the command layer and actor stamping still ride a single write path. If a future write needs
dimensions that aren't on the active lease (e.g. historical re-postings), revisit the
`ITenantPostingDimensions` contract rather than letting the client supply them.
