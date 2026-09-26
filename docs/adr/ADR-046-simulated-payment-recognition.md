# ADR-046: Recognize simulated payments from explicit bank evidence

- **Status:** Proposed
- **Date:** 2026-09-26
- **Deciders:** Maintainer review pending

## Context

Issue #455 precedes the one-time simulated payment in #456. The existing `PaymentReceived`
event immediately debits the trust bank. Stripe payment success, processor availability and
payout progress are distinct facts; even a payout marked paid can subsequently fail. The
[provider research](../research/stripe-connect-payment-lifecycle.md) records the primary sources.

The current trust equation has no processor receivable or clearing position. Posting gross
receipts against a net bank payout overstates bank cash; posting the net understates the tenant's
payment. Balanced journal lines and passing internal invariants cannot establish external bank
agreement. A linked reversal can also create a negative prepayment liability after credit is
consumed. These are demonstrated by the
[accounting evidence tests](../../tests/LeaseBook.Tests.Accounting/PaymentLifecycleEvidenceTests.cs).

## Decision

Propose a deliberately bounded simulator: isolated non-production fixtures, USD, fee-free gross
settlements, one payment per settlement, and explicit simulated bank evidence. Only that evidence
permits the existing Accounting receipt event. Provider success and payout notifications produce
durable Payments facts but no journal lines. No processor clearing account is introduced for this
simulation. This choice establishes no real-money recognition or compliance policy.

Payments owns durable operations, authenticated inbox processing, provider dispatch and business
effect deduplication. Host adapters call Accounting within the same organization transaction as
the effect record; processor calls occur outside it. A small processor interface hides transport
mechanics. The [implementation specification](../payments/simulated-payment-spec.md) owns states,
transactions, exact posting behavior, fixture barriers and executable acceptance vectors.

The first implementation records late returns as review-required facts and preserves the receipt.
It does not automatically call the unrestricted reversal service. The evidence tests demonstrate
both a simple linked reversal and the consumed-credit failure that prevents generalizing it.
Fees, netting, partial returns, refunds, historical-period corrections and real-money clearing
require subsequent accounting decisions and guarded commands before automatic posting.

## Consequences

The portal can exercise real durability, isolation and ledger integration without pretending that
a successful API response is bank cash. Pending payments do not reduce the ledger's amount due;
the UI separately shows their progress. The simulator must generate bank evidence as a distinct
step and must fail closed on unsupported money scenarios.

Live processing cannot be enabled by configuration. A future approved release must add the live
adapter, reviewed recognition/clearing/fee/return policies and their invariant changes, plus its
deployment acceptance. A clearing asset cannot simply be added to today's trust equation: any
model recognizing owner or tenant claims before bank receipt must expressly account for both
processor-held assets and the associated obligations, including losses and withheld amounts.

## Revisit trigger

Revisit before implementing a Stripe adapter that posts money, any pre-bank recognition, net
settlement, fee allocation, automatic return, or use outside the isolated simulator fixture.
