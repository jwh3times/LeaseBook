import { useEffect, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  getApiPayments,
  getApiPortalTenantPayments,
  postApiPaymentsByIdRetry,
  postApiPortalTenantPayments,
  unwrap,
  type PaymentView,
  type SubmitPaymentBody,
} from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { QueryErrorState } from '@/components/QueryErrorState';
import { Badge, Button, Card, Input, Money } from '@/design';
import { useSession } from '@/features/auth/useSession';

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
    default:
      return payment.receiptRecorded
        ? 'Simulated payment needs review — original receipt remains recorded'
        : 'Simulated payment needs review — no receipt recorded';
  }
}

const REASONS: Record<string, string> = {
  technical_failure:
    'Delivery or posting could not finish. An administrator can retry the same operation.',
  accounting_period_locked:
    'The evidence date is in a locked period. Accounting review is required; retry cannot change the date.',
  attribution_unavailable:
    'The trust bank or lease attribution is unavailable for the evidence date. Review the bank and lease records.',
  return_requires_review:
    'A return, refund or dispute needs an approved accounting decision. No automatic reversal was made.',
  unsupported_settlement:
    'Settlement includes an unsupported amount, fee or destination. Review the evidence; no force-post action is available.',
  conflicting_evidence:
    'The evidence conflicts. Review the operation before taking further action.',
  collection_failed: 'Collection failed. The tenant may submit a new simulated payment.',
  accounting_rejected: 'Accounting rejected the receipt. Review the operation and ledger.',
  fixture_unavailable:
    'The fixture binding is unavailable. Ask the fixture operator to check its configuration.',
};

export function PaymentPanel({ staff = false }: { staff?: boolean }) {
  const queries = useQueryClient();
  const session = useSession();
  const [amount, setAmount] = useState('');
  const request = useRef<SubmitPaymentBody | null>(null);
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
  const submit = useMutation({
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
  });
  const retry = useMutation({
    mutationFn: (id: string) =>
      unwrap(postApiPaymentsByIdRetry({ path: { id } }), 'Unable to retry the simulated payment.'),
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['simulated-payments'] }),
  });
  function send() {
    request.current ??= { key: crypto.randomUUID(), amount, currency: 'USD' };
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
      {staff && Number(list.data.unmatchedObservations ?? 0) > 0 && (
        <p role="status">
          {list.data.unmatchedObservations} unmatched notifications require fixture-operator review.
          Their payment mapping has been missing for more than ten minutes.
        </p>
      )}
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
          <Button
            type="submit"
            disabled={
              submit.isPending ||
              Number(amount) < 0.01 ||
              Number(amount) > 10000 ||
              !Number.isFinite(Number(amount))
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
              <p>The result is uncertain. Retrying uses the same request reference and amount.</p>
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
      <div aria-live="polite" aria-atomic="false">
        {list.data.items.length === 0 ? (
          <p>No simulated payments yet.</p>
        ) : (
          <ul>
            {list.data.items.map((payment) => (
              <li key={payment.id} className="col gap8">
                <Money value={Number(payment.amount)} />
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
                    {payment.canRetry && session.data?.role === 'PMAdmin' && (
                      <Button disabled={retry.isPending} onClick={() => retry.mutate(payment.id)}>
                        Retry operation
                      </Button>
                    )}
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
