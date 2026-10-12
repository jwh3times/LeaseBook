import { useRef, useState } from 'react';
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
import { wording, type Wording } from './wording';

/**
 * A payout's status in words. A payout that posted keeps `postedAt` when the processor later says it
 * failed, and nothing is reversed for it: in review or closed, it says which of the two it is, and
 * never "nothing posted" about a payout that did post.
 */
function status(payout: SettlementView): { label: string; tone: BadgeTone } {
  const posted = payout.postedAt != null;
  switch (payout.status) {
    case 'Received':
      return { label: 'Waiting to be checked', tone: 'neutral' };
    case 'AwaitingBank':
      return { label: 'Waiting for the bank', tone: 'neutral' };
    case 'NeedsReview':
      return {
        label: posted ? 'Needs review — posted, nothing reversed' : 'Needs review — nothing posted',
        tone: 'warn',
      };
    case 'Posted':
      return { label: 'Posted', tone: 'pos' };
    case 'Closed':
      return {
        label: posted ? 'Closed — posted, nothing reversed' : 'Closed — nothing posted',
        tone: 'neutral',
      };
    default:
      return { label: payout.status, tone: 'neutral' };
  }
}

const KINDS: Record<string, string> = {
  Payment: 'Payment',
  Return: 'Returned payment',
  Fee: 'Processor fee',
  Dispute: 'Card dispute',
  Refund: 'Refund',
  FailedDebit: 'Failed bank debit',
  Unknown: 'Unrecognised line',
};

/** A line's kind in words. A kind this page does not know is named as one, never left blank. */
function kind(value: string) {
  if (KINDS[value]) return KINDS[value];
  return value.trim() ? `Unrecognised line (${value.trim()})` : 'Unrecognised line';
}

const COLUMNS: TableColumn<SettlementItemView>[] = [
  { key: 'item', header: 'Line', render: (row) => row.item },
  { key: 'kind', header: 'Kind', render: (row) => kind(row.kind) },
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
 * The processor payouts of the host's payment mode and what became of each (ADR-053, ADR-054). A
 * payout posts completely or not at all. A clean one posts by itself; one that waits says why, and an
 * administrator posts it with one action or closes it with a note. A sandbox payout is first heard of
 * from the processor and is held, with no bank amount or date to show, until a bank says what it
 * received. Staff who are not administrators read the same list without the actions.
 */
export function PayoutBatches({
  admin,
  words = wording(null),
}: {
  admin: boolean;
  words?: Wording;
}) {
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
  // The button an action was started from is gone once the action succeeds, so focus moves to the
  // payout's name: a keyboard user stays on the payout they acted on and hears its new status.
  const names = useRef(new Map<string, HTMLElement>());
  const focusName = (id: string) => window.setTimeout(() => names.current.get(id)?.focus(), 0);
  // A refused post is a 409 that still changed the payout: its reason now names what stopped it.
  const post = useMutation({
    mutationFn: (id: string) =>
      unwrap(postApiPaymentsSettlementsByIdPost({ path: { id } }), 'Unable to post the payout.'),
    onSettled: refresh,
    onSuccess: (_, id) => {
      void queries.invalidateQueries({ queryKey: ['tenant-ledger'] });
      focusName(id);
    },
  });
  const close = useMutation({
    mutationFn: (body: { id: string; note: string }) =>
      unwrap(
        postApiPaymentsSettlementsByIdClose({ path: { id: body.id }, body: { note: body.note } }),
        'Unable to close the payout review.',
      ),
    // A refused close may still mean the payout moved, so the list is read again either way.
    onSettled: refresh,
    onSuccess: (_, body) => {
      setClosing(null);
      focusName(body.id);
    },
  });

  return (
    <section className="col gap8" aria-labelledby="payout-batches-title" aria-live="polite">
      <h4 id="payout-batches-title">Payouts</h4>
      {list.isError ? (
        <QueryErrorState
          query={list}
          title="Couldn’t load payouts"
          fallback="Unable to load payouts."
        />
      ) : list.isPending ? (
        <p role="status">Loading payouts…</p>
      ) : list.data.items.length === 0 ? (
        <p>{words.noPayouts}</p>
      ) : (
        <ul className="col gap12">
          {list.data.items.map((payout) => (
            <Payout
              key={payout.id}
              payout={payout}
              admin={admin}
              busy={post.isPending || close.isPending}
              closing={closing?.id === payout.id ? closing.note : null}
              nameRef={(element) => {
                if (element) names.current.set(payout.id, element);
                else names.current.delete(payout.id);
              }}
              // A refusal is shown on the payout it is about, and only until the next action.
              failure={
                post.isError && post.variables === payout.id ? (
                  <>
                    <ApiErrorNotice error={post.error} fallback="Unable to post the payout." />
                    <ErrorAction error={post.error} />
                  </>
                ) : close.isError && close.variables.id === payout.id ? (
                  <>
                    <ApiErrorNotice
                      error={close.error}
                      fallback="Unable to close the payout review."
                    />
                    <ErrorAction error={close.error} />
                  </>
                ) : null
              }
              onPost={() => {
                close.reset();
                post.mutate(payout.id);
              }}
              onStartClose={() => {
                post.reset();
                setClosing({ id: payout.id, note: '' });
              }}
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
  nameRef,
  failure,
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
  nameRef: (element: HTMLElement | null) => void;
  failure: React.ReactNode;
  onPost: () => void;
  onStartClose: () => void;
  onNote: (note: string) => void;
  onCancelClose: () => void;
  onClose: () => void;
}) {
  const state = status(payout);
  // Bank evidence is the amount and the date together. Without both there is none to show: a null
  // amount is not a zero, and is never rendered as one.
  const banked = payout.bankAmount != null && payout.bankDate != null;
  const name = `Payout ${payout.payoutReference}`;
  return (
    <li className="col gap8" aria-label={name}>
      <div className="row gap8 wrap">
        <strong ref={nameRef} tabIndex={-1}>
          {name}
        </strong>
        <Badge tone={state.tone} dot>
          {state.label}
        </Badge>
      </div>
      {banked ? (
        <span>
          <Money value={Number(payout.bankAmount)} /> at the bank on {payout.bankDate}
        </span>
      ) : (
        <span>
          No bank evidence has arrived for this payout.
          {payout.status === 'AwaitingBank' &&
            ' Nothing posts until the bank says what it received.'}
        </span>
      )}
      {payout.processorAmount != null && (
        <span>
          Processor’s payout amount: <Money value={Number(payout.processorAmount)} />
        </span>
      )}
      {payout.failedAt && (
        <span>
          The processor reported that this payout failed on{' '}
          <time dateTime={payout.failedAt}>{new Date(payout.failedAt).toLocaleString()}</time>.
        </span>
      )}
      {failure}
      {/* Why it waits, and what to do about it: only while it still waits. */}
      {payout.reason && (payout.status === 'NeedsReview' || payout.status === 'Received') && (
        <p>
          {payoutReason(payout.reason, payout.postedAt != null)}
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
