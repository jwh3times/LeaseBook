import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  getApiPaymentsSettlements,
  postApiPaymentsSettlementsByIdClose,
  postApiPaymentsSettlementsByIdPost,
  unwrap,
  type SettlementItemView,
  type SettlementView,
} from '@/api';
import { ApiErrorNotice } from '@/components/ApiErrorNotice';
import { ErrorAction } from '@/components/ErrorAction';
import { QueryErrorState } from '@/components/QueryErrorState';
import { Badge, type BadgeTone, Button, Input, Money, Table, type TableColumn } from '@/design';
import { payoutReason } from './reasons';

const STATUS: Record<string, { label: string; tone: BadgeTone }> = {
  Received: { label: 'Waiting to be checked', tone: 'neutral' },
  NeedsReview: { label: 'Needs review — nothing posted', tone: 'warn' },
  Posted: { label: 'Posted', tone: 'pos' },
  Closed: { label: 'Closed — nothing posted', tone: 'neutral' },
};

const KINDS: Record<string, string> = {
  Payment: 'Payment',
  Return: 'Returned payment',
  Fee: 'Processor fee',
};

const COLUMNS: TableColumn<SettlementItemView>[] = [
  { key: 'item', header: 'Line', render: (row) => row.item },
  { key: 'kind', header: 'Kind', render: (row) => KINDS[row.kind] ?? row.kind },
  {
    key: 'gross',
    header: 'Charged',
    num: true,
    render: (row) => <Money value={Number(row.gross)} />,
  },
  {
    key: 'fee',
    header: 'Processor fee',
    num: true,
    render: (row) => <Money value={Number(row.fee)} />,
  },
  {
    key: 'net',
    header: 'To the bank',
    num: true,
    render: (row) => <Money value={Number(row.net)} />,
  },
  {
    key: 'posted',
    header: 'Ledger',
    render: (row) =>
      row.entryId || row.feeEntryId
        ? row.entryId && row.feeEntryId
          ? 'On the ledger, with a fee difference'
          : row.entryId
            ? 'On the ledger'
            : 'Fee difference on the ledger'
        : 'Not on the ledger',
  },
];

const KEY = ['simulated-payments', 'settlements'] as const;

/**
 * The processor payouts of the payment simulation and what became of each (ADR-053). A payout posts
 * completely or not at all. A clean one posts by itself; one that waits says why, and an
 * administrator posts it with one action or closes it with a note. Staff who are not administrators
 * read the same list without the actions.
 */
export function PayoutBatches({ admin }: { admin: boolean }) {
  const queries = useQueryClient();
  const [closing, setClosing] = useState<{ id: string; note: string } | null>(null);
  const list = useQuery({
    queryKey: KEY,
    queryFn: () => unwrap(getApiPaymentsSettlements(), 'Unable to load payouts.'),
    retry: false,
    gcTime: 0,
    staleTime: 0,
    refetchInterval: 5000,
  });
  const refresh = () => {
    void queries.invalidateQueries({ queryKey: ['simulated-payments'] });
  };
  // A refused post is a 409 that still changed the payout: its reason now names what stopped it.
  const post = useMutation({
    mutationFn: (id: string) =>
      unwrap(postApiPaymentsSettlementsByIdPost({ path: { id } }), 'Unable to post the payout.'),
    onSettled: refresh,
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['tenant-ledger'] }),
  });
  const close = useMutation({
    mutationFn: (body: { id: string; note: string }) =>
      unwrap(
        postApiPaymentsSettlementsByIdClose({ path: { id: body.id }, body: { note: body.note } }),
        'Unable to close the payout review.',
      ),
    onSuccess: () => {
      setClosing(null);
      refresh();
    },
  });

  return (
    <section className="col gap8" aria-labelledby="payout-batches-title">
      <h4 id="payout-batches-title">Payouts</h4>
      {post.isError && (
        <>
          <ApiErrorNotice error={post.error} fallback="Unable to post the payout." />
          <ErrorAction error={post.error} />
        </>
      )}
      {close.isError && (
        <>
          <ApiErrorNotice error={close.error} fallback="Unable to close the payout review." />
          <ErrorAction error={close.error} />
        </>
      )}
      {list.isError ? (
        <QueryErrorState
          query={list}
          title="Couldn’t load payouts"
          fallback="Unable to load payouts."
        />
      ) : list.isPending ? (
        <p role="status">Loading payouts…</p>
      ) : list.data.items.length === 0 ? (
        <p>No payouts yet. A payout appears here when the bank reports it.</p>
      ) : (
        <ul className="col gap12" aria-live="polite">
          {list.data.items.map((payout) => (
            <Payout
              key={payout.id}
              payout={payout}
              admin={admin}
              busy={post.isPending || close.isPending}
              closing={closing?.id === payout.id ? closing.note : null}
              onPost={() => post.mutate(payout.id)}
              onStartClose={() => setClosing({ id: payout.id, note: '' })}
              onNote={(note) => setClosing({ id: payout.id, note })}
              onCancelClose={() => setClosing(null)}
              onClose={() => closing && close.mutate(closing)}
            />
          ))}
        </ul>
      )}
    </section>
  );
}

function Payout({
  payout,
  admin,
  busy,
  closing,
  onPost,
  onStartClose,
  onNote,
  onCancelClose,
  onClose,
}: {
  payout: SettlementView;
  admin: boolean;
  busy: boolean;
  closing: string | null;
  onPost: () => void;
  onStartClose: () => void;
  onNote: (note: string) => void;
  onCancelClose: () => void;
  onClose: () => void;
}) {
  const status = STATUS[payout.status] ?? { label: payout.status, tone: 'neutral' as BadgeTone };
  const name = `Payout ${payout.payoutReference}`;
  return (
    <li className="col gap8" aria-label={name}>
      <div className="row gap8 wrap">
        <strong>{name}</strong>
        <Badge tone={status.tone} dot>
          {status.label}
        </Badge>
      </div>
      <span>
        <Money value={Number(payout.bankAmount)} /> at the bank on {payout.bankDate}
      </span>
      {payout.reason && payout.status !== 'Posted' && (
        <p>
          {payoutReason(payout.reason)}
          {payout.reasonItem ? ` Line: ${payout.reasonItem}.` : ''}
        </p>
      )}
      {payout.reason && <span>Reason: {payout.reason}</span>}
      {payout.reviewNote && <span>Review closed by staff: {payout.reviewNote}</span>}
      <Table columns={COLUMNS} rows={payout.items} rowKey={(row) => row.item} />
      {(payout.canPost || payout.canClose) && !admin && (
        <p className="t3 fs13">An administrator can act on this payout.</p>
      )}
      {admin && payout.canPost && (
        <div>
          <Button
            disabled={busy}
            onClick={onPost}
            aria-label={`Post payout ${payout.payoutReference}`}
          >
            Post payout
          </Button>
        </div>
      )}
      {admin &&
        payout.canClose &&
        (closing !== null ? (
          <form
            className="col gap8"
            onSubmit={(event) => {
              event.preventDefault();
              onClose();
            }}
          >
            <label htmlFor={`close-payout-${payout.id}`}>How was this resolved? (staff only)</label>
            <Input
              id={`close-payout-${payout.id}`}
              value={closing}
              maxLength={500}
              required
              autoFocus
              onChange={(event) => onNote(event.target.value)}
            />
            <p>Closing posts nothing. The evidence and this note are kept.</p>
            <div className="row gap8">
              <Button type="submit" disabled={busy || closing.trim() === ''}>
                Close review
              </Button>
              <Button type="button" onClick={onCancelClose}>
                Cancel
              </Button>
            </div>
          </form>
        ) : (
          <div>
            <Button
              onClick={onStartClose}
              aria-label={`Close review of payout ${payout.payoutReference}`}
            >
              Close review…
            </Button>
          </div>
        ))}
    </li>
  );
}
