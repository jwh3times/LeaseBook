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
import { PayoutBatches } from './PayoutBatches';
import { paymentReason, tenantReviewNote } from './reasons';
import { UnmatchedObservations } from './UnmatchedObservations';
import { wording, type Wording } from './wording';

function description(payment: PaymentView, words: Wording) {
  const status = words.status;
  switch (payment.status) {
    case 'Requested':
      return status.requested;
    case 'Processing':
      return payment.paidAt
        ? `Paid on ${payment.paidAt.slice(0, 10)} — on its way to the bank, not yet on your ledger`
        : status.processing;
    case 'Failed':
      return status.failed;
    case 'Settled':
      return status.settled;
    case 'Returned':
      return status.returned;
    case 'ReviewClosed':
      return payment.receiptRecorded
        ? status.reviewClosedWithReceipt
        : status.reviewClosedNoReceipt;
    default:
      return payment.receiptRecorded ? status.needsReviewWithReceipt : status.needsReviewNoReceipt;
  }
}

const METHODS = [
  { value: 'ach', label: 'Bank debit (ACH)' },
  { value: 'card', label: 'Card' },
];
const AMOUNT = /^(?:0|[1-9][0-9]{0,4})(?:\.[0-9]{1,2})?$/;

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
  // The mode the list last reported. Until a read succeeds it is unknown, and reads as the simulation.
  const mode = useRef<string | null | undefined>(undefined);
  const list = useQuery({
    queryKey: ['simulated-payments', staff],
    queryFn: () =>
      unwrap(
        staff ? getApiPayments() : getApiPortalTenantPayments(),
        wording(mode.current).loadFailed,
      ),
    retry: false,
    gcTime: 0,
    staleTime: 0,
    refetchInterval: (query) => (query.state.data?.enabled ? 2000 : false),
  });
  const words = wording(list.data?.mode);
  const sandbox = list.data?.mode === 'StripeSandbox';
  useEffect(() => {
    if (list.data) mode.current = list.data.mode;
  }, [list.data]);
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
      unwrap(postApiPortalTenantPayments({ body }), words.submitFailed),
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
      unwrap(postApiPaymentsByIdRetry({ path: { id } }), words.retryFailed),
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
      <QueryErrorState query={list} title={words.loadFailedTitle} fallback={words.loadFailed} />
    );
  if (list.isPending) return <p aria-live="polite">Loading payment availability…</p>;
  if (!list.data.enabled) return null;
  return (
    <Card>
      <h3>{words.heading}</h3>
      <Badge dot>{words.badge}</Badge>
      {Number(list.data.fundsInTransit ?? 0) > 0 && (
        <p role="status">
          In transit: <Money value={Number(list.data.fundsInTransit)} />.{' '}
          {staff
            ? 'Collected by the processor and not yet at the bank. Tenant balances and the bank book do not include it.'
            : 'Paid and on its way to the bank. Your balance still includes it until it arrives.'}
        </p>
      )}
      {staff && <UnmatchedObservations count={Number(list.data.unmatchedObservations ?? 0)} />}
      {staff && <PayoutBatches admin={admin} words={words} />}
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
                : words.submit}
          </Button>
          {submit.isError && (
            <>
              <ApiErrorNotice error={submit.error} fallback={words.submitFailed} />
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
          <ApiErrorNotice error={retry.error} fallback={words.retryFailed} />
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
          <p>{words.empty}</p>
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
                <span>{description(payment, words)}</span>
                {/* A tenant is never shown a reason code or the staff text for one. The simulation
                    keeps its screens as they were, so only the sandbox adds this sentence. */}
                {!staff && sandbox && payment.status === 'NeedsReview' && (
                  <span>{tenantReviewNote(payment)}</span>
                )}
                <span>Payment reference: {payment.id}</span>
                {staff && (
                  <>
                    {payment.reason && <p>{paymentReason(payment, words)}</p>}
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
