import { useEffect, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  getApiPayments,
  getApiPortalTenantPayments,
  getApiPortalTenantPaymentsQuote,
  postApiPaymentsByIdCloseReview,
  postApiPaymentsByIdRetry,
  postApiPaymentsByIdReturn,
  postApiPortalTenantPayments,
  unwrap,
  type ApiError,
  type PaymentView,
  type SubmitPaymentBody,
} from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { QueryErrorState } from '@/components/QueryErrorState';
import { Badge, Button, Card, Input, Money, Select } from '@/design';
import { useSession } from '@/features/auth/useSession';
import { UnmatchedObservations } from './UnmatchedObservations';

function description(payment: PaymentView) {
  switch (payment.status) {
    case 'Requested':
      return 'Simulation requested';
    case 'Processing':
      return 'Simulated payment processing — not yet on your ledger';
    case 'Failed':
      return 'Simulated payment failed — no payment recorded';
    case 'Settled':
      return 'Simulated payment recorded';
    case 'Returned':
      return 'Simulated payment returned by the bank — the receipt was reversed';
    case 'ReviewClosed':
      return payment.receiptRecorded
        ? 'Simulated payment review closed — original receipt remains recorded'
        : 'Simulated payment review closed — no receipt recorded';
    default:
      return payment.receiptRecorded
        ? 'Simulated payment needs review — original receipt remains recorded'
        : 'Simulated payment needs review — no receipt recorded';
  }
}

const METHODS = [
  { value: 'ach', label: 'Bank debit (ACH)' },
  { value: 'card', label: 'Card' },
];
const AMOUNT = /^(?:0|[1-9][0-9]{0,4})(?:\.[0-9]{1,2})?$/;

const REASONS: Record<string, string> = {
  technical_failure:
    'Delivery or posting could not finish. An administrator can retry the same operation.',
  accounting_period_locked:
    'The evidence date is in a locked period. Accounting review is required; retry cannot change the date.',
  attribution_unavailable:
    'The trust bank or lease attribution is unavailable for the evidence date. Review the bank and lease records.',
  return_requires_review:
    'A return, refund or dispute arrived after the receipt posted. No automatic reversal was made. An administrator can post a full return, or close the review after correcting the ledger.',
  return_prepayment_consumed:
    'The return was not posted: part of this payment became prepaid credit that has since been used. Correct the ledger, then close the review.',
  return_owner_funds_disbursed:
    'The return was not posted: funds from this payment have since left the owner’s balance. Correct the ledger, then close the review.',
  return_precedes_receipt:
    'The return was not posted: it is dated before the payment it returns. Review the evidence, then close the review.',
  evidence_after_return:
    'More return, refund or dispute evidence arrived after the return was posted. Review the evidence and the ledger, then close the review.',
  return_period_locked:
    'The return was not posted: it is dated in a locked period, and the date cannot be changed. Correct the ledger, then close the review.',
  return_partial_unsupported:
    'The return was not posted: only a full return can be posted. Correct the ledger, then close the review.',
  return_kind_unsupported:
    'The return was not posted: a refund or dispute cannot be posted as a return. Correct the ledger, then close the review.',
  return_conflicting_evidence:
    'The return was not posted: the return evidence conflicts. Review the evidence, then close the review.',
  return_rejected:
    'The return was not posted: accounting refused it. Review the ledger, then close the review.',
  unsupported_settlement:
    'Settlement includes an unsupported amount, fee or destination. Review the evidence; no force-post action is available.',
  conflicting_evidence:
    'The evidence conflicts. Review the operation before taking further action.',
  collection_failed: 'Collection failed. The tenant may submit a new simulated payment.',
  accounting_rejected: 'Accounting rejected the receipt. Review the operation and ledger.',
  fixture_unavailable:
    'The fixture binding is unavailable. Ask the fixture operator to check its configuration.',
};

// What the tenant confirms: the fee on top of the amount going to their ledger, and the total charged.
function QuoteLine({ fee, charged }: { fee: number; charged: number }) {
  return (
    <p>
      {fee > 0 ? (
        <>
          Convenience fee: <Money value={fee} />
        </>
      ) : (
        'No convenience fee'
      )}{' '}
      · Total charged: <Money value={charged} />
    </p>
  );
}

export function PaymentPanel({ staff = false }: { staff?: boolean }) {
  const queries = useQueryClient();
  const session = useSession();
  const [amount, setAmount] = useState('');
  const [method, setMethod] = useState('ach');
  const request = useRef<SubmitPaymentBody | null>(null);
  const [closing, setClosing] = useState<{ id: string; note: string } | null>(null);
  const list = useQuery({
    queryKey: ['simulated-payments', staff],
    queryFn: () =>
      unwrap(
        staff ? getApiPayments() : getApiPortalTenantPayments(),
        'Unable to load simulated payments.',
      ),
    retry: false,
    gcTime: 0,
    staleTime: 0,
    refetchInterval: (query) => (query.state.data?.enabled ? 2000 : false),
  });
  const recorded = list.data?.items
    .filter((p) => p.receiptRecorded)
    .map((p) => p.id)
    .join(',');
  useEffect(() => {
    if (recorded) void queries.invalidateQueries({ queryKey: ['resident-ledger'] });
  }, [recorded, queries]);
  const priceable = AMOUNT.test(amount) && Number(amount) >= 0.01 && Number(amount) <= 10000;
  // The fee is the server's: the tenant confirms the figure shown here, and the server refuses the
  // request if its rule no longer gives that figure. A request already sent keeps the fee it carried.
  const quote = useQuery({
    queryKey: ['simulated-payment-quote', amount, method],
    queryFn: () =>
      unwrap(
        getApiPortalTenantPaymentsQuote({ query: { amount, method } }),
        'Unable to calculate the fee for this payment.',
      ),
    enabled: !staff && priceable && request.current === null && list.data?.enabled === true,
    retry: false,
    gcTime: 0,
    staleTime: 0,
  });
  const submit = useMutation<PaymentView, ApiError, SubmitPaymentBody>({
    mutationFn: (body: SubmitPaymentBody) =>
      unwrap(
        postApiPortalTenantPayments({ body }),
        'Unable to confirm the simulated payment request.',
      ),
    onSuccess: () => {
      request.current = null;
      setAmount('');
      void queries.invalidateQueries({ queryKey: ['simulated-payments'] });
    },
    onError: (error) => {
      // Refused outright, so nothing is uncertain: drop the request and show the fee as it is now.
      if (error.code === 'fee_quote_changed') {
        request.current = null;
        void queries.invalidateQueries({ queryKey: ['simulated-payment-quote'] });
      }
    },
  });
  const feeChanged = submit.isError && submit.error.code === 'fee_quote_changed';
  const retry = useMutation({
    mutationFn: (id: string) =>
      unwrap(postApiPaymentsByIdRetry({ path: { id } }), 'Unable to retry the simulated payment.'),
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['simulated-payments'] }),
  });
  // A refused return is a 409 that still changed the payment: its reason now names what stopped it.
  const postReturn = useMutation({
    mutationFn: (id: string) =>
      unwrap(postApiPaymentsByIdReturn({ path: { id } }), 'Unable to post the return.'),
    onSettled: () => void queries.invalidateQueries({ queryKey: ['simulated-payments'] }),
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['tenant-ledger'] }),
  });
  const closeReview = useMutation({
    mutationFn: (body: { id: string; note: string }) =>
      unwrap(
        postApiPaymentsByIdCloseReview({ path: { id: body.id }, body: { note: body.note } }),
        'Unable to close the review.',
      ),
    onSuccess: () => {
      setClosing(null);
      void queries.invalidateQueries({ queryKey: ['simulated-payments'] });
    },
  });
  const admin = session.data?.role === 'PMAdmin';
  function send() {
    if (request.current === null) {
      if (!quote.isSuccess) return;
      request.current = {
        key: crypto.randomUUID(),
        amount,
        currency: 'USD',
        method,
        quotedFee: quote.data.fee,
      };
    }
    submit.mutate(request.current);
  }
  if (list.isError)
    return (
      <QueryErrorState
        query={list}
        title="Couldn’t load simulated payments"
        fallback="Unable to load simulated payments."
      />
    );
  if (list.isPending) return <p aria-live="polite">Loading payment availability…</p>;
  if (!list.data.enabled) return null;
  return (
    <Card>
      <h3>Simulated payments</h3>
      <Badge dot>Simulation — no real money moves</Badge>
      {staff && <UnmatchedObservations count={Number(list.data.unmatchedObservations ?? 0)} />}
      {!staff && (
        <form
          className="col gap12"
          onSubmit={(event) => {
            event.preventDefault();
            send();
          }}
        >
          <p>
            Pending requests do not reduce your ledger balance. An excess payment becomes prepaid
            credit.
          </p>
          <label htmlFor="simulation-amount">Amount (USD)</label>
          <Input
            id="simulation-amount"
            inputMode="decimal"
            value={amount}
            required
            pattern="(?:0|[1-9][0-9]{0,4})(?:\.[0-9]{1,2})?"
            readOnly={request.current !== null}
            onChange={(event) => setAmount(event.target.value)}
            aria-describedby="simulation-limits"
          />
          <p id="simulation-limits">Enter $0.01 to $10,000.00.</p>
          <label htmlFor="simulation-method">Pay with</label>
          <Select
            id="simulation-method"
            value={method}
            disabled={request.current !== null}
            onChange={(event) => setMethod(event.target.value)}
          >
            {METHODS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </Select>
          <div id="simulation-quote" aria-live="polite">
            {request.current !== null ? (
              <QuoteLine
                fee={Number(request.current.quotedFee ?? 0)}
                charged={Number(request.current.amount) + Number(request.current.quotedFee ?? 0)}
              />
            ) : !priceable ? null : quote.isError ? (
              <>
                <ApiErrorNotice
                  error={quote.error}
                  fallback="Unable to calculate the fee for this payment."
                  kind="read"
                />
                <ErrorAction
                  error={quote.error}
                  onRetry={() => void quote.refetch()}
                  retrying={quote.isFetching}
                />
              </>
            ) : quote.isSuccess ? (
              <QuoteLine fee={Number(quote.data.fee)} charged={Number(quote.data.charged)} />
            ) : (
              <p>Calculating the fee…</p>
            )}
          </div>
          <Button
            type="submit"
            aria-describedby="simulation-quote"
            disabled={
              submit.isPending || (request.current === null && !(priceable && quote.isSuccess))
            }
          >
            {submit.isPending
              ? 'Submitting…'
              : request.current
                ? 'Retry same payment request'
                : 'Submit simulated payment'}
          </Button>
          {submit.isError && (
            <>
              <ApiErrorNotice
                error={submit.error}
                fallback="Unable to confirm the simulated payment request."
              />
              {feeChanged ? (
                <p>Nothing was requested. Check the fee shown above, then submit again.</p>
              ) : (
                <p>The result is uncertain. Retrying uses the same request reference and amount.</p>
              )}
              <ErrorAction error={submit.error} />
            </>
          )}
        </form>
      )}
      {retry.isError && (
        <>
          <ApiErrorNotice error={retry.error} fallback="Unable to retry the simulated payment." />
          <ErrorAction error={retry.error} />
        </>
      )}
      {postReturn.isError && (
        <>
          <ApiErrorNotice error={postReturn.error} fallback="Unable to post the return." />
          <ErrorAction error={postReturn.error} />
        </>
      )}
      {closeReview.isError && (
        <>
          <ApiErrorNotice error={closeReview.error} fallback="Unable to close the review." />
          <ErrorAction error={closeReview.error} />
        </>
      )}
      <div aria-live="polite" aria-atomic="false">
        {list.data.items.length === 0 ? (
          <p>No simulated payments yet.</p>
        ) : (
          <ul>
            {list.data.items.map((payment) => (
              <li key={payment.id} className="col gap8">
                <Money value={Number(payment.amount)} />
                {Number(payment.quotedFee ?? 0) > 0 && (
                  <span>
                    Plus a <Money value={Number(payment.quotedFee)} /> convenience fee:{' '}
                    <Money value={Number(payment.chargedAmount)} /> charged
                  </span>
                )}
                <span>{description(payment)}</span>
                <span>Payment reference: {payment.id}</span>
                {staff && (
                  <>
                    {payment.reason && (
                      <p>
                        {REASONS[payment.reason] ??
                          'Review required. Contact the fixture operator.'}
                      </p>
                    )}
                    <span>Reason: {payment.reason ?? 'None'}</span>
                    {payment.evidenceReference && (
                      <span>Evidence reference: {payment.evidenceReference}</span>
                    )}
                    {payment.lastAttemptAt && <span>Last attempt: {payment.lastAttemptAt}</span>}
                    {payment.receiptEntryId && <span>Receipt entry: {payment.receiptEntryId}</span>}
                    {payment.returnEntryId && <span>Reversal entry: {payment.returnEntryId}</span>}
                    {payment.reviewNote && (
                      <span>Review closed by staff: {payment.reviewNote}</span>
                    )}
                    {payment.canRetry && admin && (
                      <Button disabled={retry.isPending} onClick={() => retry.mutate(payment.id)}>
                        Retry operation
                      </Button>
                    )}
                    {payment.canPostReturn && admin && (
                      <Button
                        disabled={postReturn.isPending}
                        onClick={() => postReturn.mutate(payment.id)}
                      >
                        Post return
                      </Button>
                    )}
                    {payment.canCloseReview &&
                      admin &&
                      (closing?.id === payment.id ? (
                        <form
                          className="col gap8"
                          onSubmit={(event) => {
                            event.preventDefault();
                            closeReview.mutate(closing);
                          }}
                        >
                          <label htmlFor={`close-note-${payment.id}`}>
                            How was this resolved? (staff only)
                          </label>
                          <Input
                            id={`close-note-${payment.id}`}
                            value={closing.note}
                            maxLength={500}
                            required
                            autoFocus
                            onChange={(event) =>
                              setClosing({ id: payment.id, note: event.target.value })
                            }
                          />
                          <p>Closing posts nothing. The evidence and this note are kept.</p>
                          <div className="row gap8">
                            <Button
                              type="submit"
                              disabled={closeReview.isPending || closing.note.trim() === ''}
                            >
                              Close review
                            </Button>
                            <Button type="button" onClick={() => setClosing(null)}>
                              Cancel
                            </Button>
                          </div>
                        </form>
                      ) : (
                        <Button onClick={() => setClosing({ id: payment.id, note: '' })}>
                          Close review…
                        </Button>
                      ))}
                  </>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>
    </Card>
  );
}
