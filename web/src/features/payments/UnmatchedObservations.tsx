import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { getApiPaymentsUnmatched, unwrap, type UnmatchedObservationView } from '@/api';
import { QueryErrorState } from '@/components/QueryErrorState';
import { Button, Money, Table, type TableColumn } from '@/design';

function age(minutes: number) {
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  return hours < 24
    ? `${hours} h ${minutes % 60} min`
    : `${Math.floor(hours / 24)} d ${hours % 24} h`;
}

const COLUMNS: TableColumn<UnmatchedObservationView>[] = [
  {
    key: 'received',
    header: 'Received',
    render: (row) => (
      <time dateTime={row.receivedAt}>{new Date(row.receivedAt).toLocaleString()}</time>
    ),
  },
  { key: 'kind', header: 'Kind', render: (row) => row.kind },
  {
    key: 'amount',
    header: 'Amount',
    num: true,
    render: (row) => <Money value={Number(row.amount)} />,
  },
  { key: 'reference', header: 'Provider reference', render: (row) => row.providerReference },
  { key: 'age', header: 'Age', num: true, render: (row) => age(Number(row.ageMinutes)) },
];

/**
 * The simulated bank and processor notifications that no payment answers for (#491). Read-only: staff
 * can see what arrived, not attach it to a payment. The list is read only once it is opened.
 */
export function UnmatchedObservations({ count }: { count: number }) {
  const [open, setOpen] = useState(false);
  const list = useQuery({
    queryKey: ['simulated-payments', 'unmatched'],
    queryFn: () => unwrap(getApiPaymentsUnmatched(), 'Unable to load unmatched notifications.'),
    enabled: open,
    retry: false,
    gcTime: 0,
    staleTime: 0,
    refetchInterval: open ? 5000 : false,
  });
  if (count === 0) return null;
  return (
    <section className="col gap8">
      <p role="status">
        {count} unmatched notifications require fixture-operator review. Their payment mapping has
        been missing for more than ten minutes.
      </p>
      <div>
        <Button
          aria-expanded={open}
          aria-controls="unmatched-notifications"
          onClick={() => setOpen((shown) => !shown)}
        >
          {open ? 'Hide unmatched notifications' : 'Show unmatched notifications'}
        </Button>
      </div>
      {open && (
        <div id="unmatched-notifications" aria-live="polite">
          {list.isError ? (
            <QueryErrorState
              query={list}
              title="Couldn’t load unmatched notifications"
              fallback="Unable to load unmatched notifications."
            />
          ) : list.isPending ? (
            <p>Loading unmatched notifications…</p>
          ) : list.data.items.length === 0 ? (
            <p>No unmatched notifications.</p>
          ) : (
            <>
              <Table columns={COLUMNS} rows={list.data.items} rowKey={(row) => row.id} />
              {count > list.data.items.length && (
                <p>
                  Showing the newest {list.data.items.length} of {count}.
                </p>
              )}
            </>
          )}
        </div>
      )}
    </section>
  );
}
