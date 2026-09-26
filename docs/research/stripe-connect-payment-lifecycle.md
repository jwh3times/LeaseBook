# Stripe Connect payment lifecycle evidence

- **Audience:** Maintainers
- **Status:** Research for issue #455; provider evidence, not live-integration approval
- **Owner:** Maintainers
- **Last reviewed:** 2026-09-26

## Scope and conclusion

This note separates Stripe's provider states from LeaseBook's proposed accounting decisions.
All linked primary sources were checked on 2026-09-26. No Stripe account was provisioned and no
payment was attempted. These sources establish API behavior; they do not establish that any
funds flow satisfies trust-account requirements or when LeaseBook must recognize a receipt.

**A Stripe payout marked `paid` is not proof of irrevocable bank settlement.** Stripe explicitly
allows a payout to change from `paid` to `failed`. `arrival_date` is an expected arrival date;
`failure_balance_transaction` identifies the return of a failed payout to the Stripe balance.
([Payout object](https://docs.stripe.com/api/payouts/object))

## Charge ownership and funds flow

| Model                         | Charge and funds                                                                                                          | Refund/dispute principal                                  |
| ----------------------------- | ------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------- |
| Direct charge                 | Charge belongs to the connected account; payment increases its Stripe balance. Application fees can move to the platform. | Debited from the connected account.                       |
| Destination charge            | Platform owns the charge and transfers a portion to one connected account.                                                | Debited from the platform; transfer recovery is separate. |
| Separate charge and transfers | Platform owns the charge and independently transfers funds to one or more connected accounts.                             | Debited from the platform; transfer recovery is separate. |

Stripe presents direct charges as suitable for SaaS platforms. Direct-charge fee billing is
configurable; do not assume that Stripe fees always come out of the connected account. Indirect
charges generally charge fees to the platform. `on_behalf_of` changes the business of record and
some settlement/statement behavior; it does not move indirect-charge refund/dispute liability to
the connected account.
([Connect charge models](https://docs.stripe.com/connect/charges),
[Direct-charge fee behavior](https://docs.stripe.com/connect/direct-charges-fee-payer-behavior))

**Design implication:** a simulator can choose a direct-charge-shaped flow without claiming that
live Connect account configuration, fee allocation, banking arrangements, or legal suitability
has been approved. A transfer between Stripe balances is distinct from a payout to a bank.

## Payment success, availability, and settlement are different facts

A PaymentIntent can require a payment method, confirmation, or additional customer action; bank
debits can remain `processing`. A failed attempt can return to `requires_payment_method` for a
retry. Cancellation of an ACH intent while processing has a limited window and can fail.
`Succeeded` describes completion of the provider payment flow, not a bank payout.
([Intent lifecycle](https://docs.stripe.com/payments/paymentintents/lifecycle))

A BalanceTransaction describes a change to the Stripe balance. It records gross `amount`, `fee`,
`net` (`amount - fee`), `currency`, `source`, and `status` (`pending` or `available`). `available_on`
is availability in Stripe. These fields do not identify a deposit on the external bank statement.
([BalanceTransaction object](https://docs.stripe.com/api/balance_transactions/object))

A payout begins `pending`, becomes `in_transit` on submission to the bank, and can later become
`paid`, `failed`, or `canceled`. Its destination identifies the bank account/card. The payout's own
balance transaction describes movement out of Stripe; its existence does not prove receipt by
the bank. A failed payout can restore funds to Stripe without a customer payment failing.
([Payout object](https://docs.stripe.com/api/payouts/object))

**Accounting design implication:** retain separate facts for provider payment success, processor
availability, payout progress, and bank evidence. If LeaseBook chooses bank-evidenced recognition,
that is a product/accounting decision, not a claim that Stripe has a webhook guaranteeing final
settlement. A later bank debit or customer return still requires its own evidence and treatment.

## ACH failures, disputes, and refunds

ACH can fail after funds become available; Stripe removes those funds. Failure received after a
PaymentIntent succeeded can become a dispute. A disputed mandate cannot simply be reused for a
new debit. ACH refunds are asynchronous separate credits, and failure returns funds to Stripe.
A refund and a bank dispute can overlap and double-credit the customer.
([ACH Direct Debit](https://docs.stripe.com/payments/ach-direct-debit))

The current ACH page is internally inconsistent about dispute contestability: its dispute-process
section calls ACH disputes final, while later response/outcome sections describe evidence and
favorable outcomes. Do not derive a universal automatic outcome or an irrevocability deadline
from that page. Late returns and authorization inquiries also exist. A future live adapter needs
provider clarification for the precise supported dispute types; the simulator can model explicit
failure and recovery evidence without promising legal or network finality.
([ACH dispute sections](https://docs.stripe.com/payments/ach-direct-debit#disputes))

**Design implication:** payment status, return/dispute status, refund status, and settlement status
must not collapse into a single increasing enum. A retry after a confirmed failed collection is a
new business attempt; an uncertain network response is recovery of the original attempt.

## Webhook authentication, attribution, and delivery

Stripe signature verification needs the unmodified raw request body, the `Stripe-Signature`
header, and the endpoint signing secret. Delivery order is not guaranteed. Events can be delivered
more than once, and distinct event objects can describe the same underlying effect. Stripe
recommends event-ID deduplication, with object ID plus event type useful for semantic duplicates.
Live delivery retries run for up to three days; manual resend does not cancel automatic retries.
Event timestamps have second precision and cannot safely establish total order.
([Webhook guidance](https://docs.stripe.com/webhooks))

Connect events carry a top-level `account` identifying the connected account that owns the object.
Retrieve that object in its owning account context. Production Connect webhook URLs can receive
both test and live events, so inspect `livemode`.
([Connect webhooks](https://docs.stripe.com/connect/webhooks))

Server-side connected-account API requests use the `Stripe-Account` header.
([Connected-account authentication](https://docs.stripe.com/connect/authentication))

**Design implication:** verify authenticity before trusting event fields. Resolve organization and
banking destination from a stored provider-account binding, then establish RLS context. Do not
use caller-supplied organization metadata as authority. Persist an inbox receipt before returning
success; atomically record the business effect, journal link, and processed marker. Event-ID
uniqueness alone cannot prevent different notifications from posting the same receipt twice.

## Idempotency and recovery

Stripe caches an idempotent request's first status/body, including a `500`. Reuse with different
parameters is rejected. Keys may be pruned after at least 24 hours, after which reuse can create
a new request. Validation/concurrent-execution conflicts can occur before a result is cached.
([Idempotent requests](https://docs.stripe.com/api/idempotent_requests))

**Design implication:** preserve a durable local attempt ID, request fingerprint, provider account,
mode, provider-object IDs, and operation key. Do not resolve an unknown result by creating a new
attempt or endlessly resubmitting an expired provider key. Reconcile uncertain outcomes against
provider evidence. Keep local posting uniqueness for the full record-retention lifetime.

## Reconciliation and fee evidence

Automatic payouts can combine several transactions. The reconciliation API lists associated
BalanceTransactions using the payout ID; callers must paginate. Manual payouts do not have a
Stripe-provided allocation to underlying transactions.
([Payout reconciliation API](https://docs.stripe.com/payouts/reconciliation))

The reconciliation report provides gross, fee, and net breakdowns and failed-payout information.
Instant payouts similarly require the integration to reconcile against transaction history.
([Payout reconciliation report](https://docs.stripe.com/reports/payout-reconciliation))

**Design implication:** reconcile bank amount to the complete batch, including fees, refunds,
returns, adjustments, and reserves. Do not infer gross rent from net cash or assign an unexplained
fee to an owner/tenant. A gross-only simulator should reject a netted or incomplete batch rather
than silently invent fee policy. Fee handling and live account configuration remain explicit
future decisions.

## Simulator evidence scenarios

The following are proposed contract tests derived from the distinctions above, not a claim that
the simulator emulates every Stripe endpoint:

- Processing, confirmed failure, cancellation, and deliberate retry leave no bank receipt.
- Payment success and balance availability alone leave bank cash unchanged.
- Provider payout `paid` without bank evidence leaves recognition pending.
- Payout failure, including after provider `paid`, restores processor state without fabricating
  a customer return or a bank transaction.
- Confirmed bank evidence and a completely attributed batch produce one accounting effect.
- Duplicate notifications, reordered notifications, and a crash after posting produce no second
  effect; a different event ID describing the same settlement also produces no second effect.
- A late customer return is separate from payout failure and preserves the original receipt.
- Refund completion and return evidence cannot double-reverse the same principal automatically.
- Wrong account, wrong mode, invalid signature, changed amount, partial batch, and unexplained fee
  produce explicit rejection or investigation states.
