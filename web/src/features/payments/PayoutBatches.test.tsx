import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '@/test/mocks/server';
import { PayoutBatches } from './PayoutBatches';

function show(admin = true) {
  render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <PayoutBatches admin={admin} />
    </QueryClientProvider>,
  );
}

const line = (item: string, kind: string, gross: number, fee: number, net: number, extra = {}) => ({
  item,
  kind,
  paymentId: `payment-${item}`,
  gross,
  fee,
  net,
  entryId: null,
  feeEntryId: null,
  ...extra,
});

const payout = (extra: Record<string, unknown> = {}) => ({
  id: 'settlement-1',
  payoutReference: 'po_1',
  bankDate: '2026-02-06',
  bankAmount: 992.5,
  status: 'Posted',
  reason: null,
  reasonItem: null,
  createdAt: '2026-02-06T12:00:00Z',
  postedAt: '2026-02-06T12:00:01Z',
  reviewNote: null,
  reviewClosedAt: null,
  canPost: false,
  canClose: false,
  items: [
    line('1', 'Payment', 515.24, 22.74, 492.5, { entryId: 'e1', feeEntryId: 'f1' }),
    line('2', 'Payment', 500, 0, 500, { entryId: 'e2' }),
  ],
  ...extra,
});

const waiting = payout({
  status: 'NeedsReview',
  reason: 'settlement_requires_confirmation',
  postedAt: null,
  bankAmount: -200,
  canPost: true,
  canClose: true,
  items: [line('1', 'Return', 600, 0, -600), line('2', 'Payment', 400, 0, 400)],
});

describe('payout batches', () => {
  it('says so when there are none, and when the list cannot be read', async () => {
    let fail = false;
    server.use(
      http.get('/api/payments/settlements', () =>
        fail
          ? HttpResponse.json({ detail: 'Payout storage is unavailable.' }, { status: 503 })
          : HttpResponse.json({ items: [] }),
      ),
    );
    show();
    expect(screen.getByRole('status')).toHaveTextContent('Loading payouts…');
    expect(await screen.findByText(/No payouts yet/)).toBeVisible();

    fail = true;
    expect(
      await screen.findByText('Payout storage is unavailable.', {}, { timeout: 8000 }),
    ).toBeVisible();
    expect(screen.queryByText(/No payouts yet/)).not.toBeInTheDocument();
  }, 12000);

  it('shows a posted payout with each line, in words, and offers no action on it', async () => {
    server.use(
      http.get('/api/payments/settlements', () => HttpResponse.json({ items: [payout()] })),
    );
    show();
    const item = await screen.findByRole('listitem', { name: 'Payout po_1' });
    expect(within(item).getByText('Posted')).toBeVisible();
    expect(item).toHaveTextContent('$992.50 at the bank on 2026-02-06');
    const rows = within(item).getAllByRole('row').slice(1);
    expect(
      rows.map((row) =>
        within(row)
          .getAllByRole('cell')
          .map((cell) => cell.textContent),
      ),
    ).toEqual([
      ['1', 'Payment', '$515.24', '$22.74', '$492.50', 'On the ledger, with a fee difference'],
      ['2', 'Payment', '$500.00', '—', '$500.00', 'On the ledger'],
    ]);
    expect(within(item).queryByRole('button')).not.toBeInTheDocument();
  });

  it('lets an administrator post a waiting payout from the keyboard and leaves focus on that payout', async () => {
    let current = waiting;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/payments/settlements', () => HttpResponse.json({ items: [current] })),
      http.post('/api/payments/settlements/settlement-1/post', () => {
        current = payout({
          bankAmount: -200,
          items: [
            line('1', 'Return', 600, 0, -600, { entryId: 'e1' }),
            line('2', 'Payment', 400, 0, 400, { entryId: 'e2' }),
          ],
        });
        return HttpResponse.json(current);
      }),
    );
    show();
    const item = await screen.findByRole('listitem', { name: 'Payout po_1' });
    expect(within(item).getByText('Needs review — nothing posted')).toBeVisible();
    expect(item).toHaveTextContent('This payout contains a returned payment');
    expect(within(item).getAllByRole('row')[1]).toHaveTextContent('Returned payment');

    within(item).getByRole('button', { name: 'Post payout po_1' }).focus();
    await userEvent.keyboard('{Enter}');

    await waitFor(() =>
      expect(
        within(screen.getByRole('listitem', { name: 'Payout po_1' })).getByText('Posted'),
      ).toBeVisible(),
    );
    const after = screen.getByRole('listitem', { name: 'Payout po_1' });
    expect(after).not.toHaveTextContent('This payout contains a returned payment');
    expect(within(after).queryByRole('button')).not.toBeInTheDocument();
    await waitFor(() => expect(within(after).getByText('Payout po_1')).toHaveFocus());
  });

  it('shows a refusal on the payout it is about, with the new reason, and clears it on the next action', async () => {
    let current = waiting;
    const other = payout({ ...waiting, id: 'settlement-2', payoutReference: 'po_2' });
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/payments/settlements', () => HttpResponse.json({ items: [current, other] })),
      http.post('/api/payments/settlements/settlement-1/post', () => {
        current = payout({
          ...waiting,
          reason: 'return_owner_funds_disbursed',
          reasonItem: '1',
          canPost: false,
        });
        return HttpResponse.json(
          { title: 'return_owner_funds_disbursed', detail: 'The return was refused.' },
          { status: 409 },
        );
      }),
    );
    show();
    await userEvent.click(await screen.findByRole('button', { name: 'Post payout po_1' }));

    const refused = screen.getByRole('listitem', { name: 'Payout po_1' });
    expect(await within(refused).findByText('The return was refused.')).toBeVisible();
    expect(
      within(screen.getByRole('listitem', { name: 'Payout po_2' })).queryByText(
        'The return was refused.',
      ),
    ).not.toBeInTheDocument();
    await waitFor(() =>
      expect(refused).toHaveTextContent(
        'A return in this payout cannot be posted. The return was not posted: funds from this payment have since left the owner’s balance.',
      ),
    );
    expect(refused).toHaveTextContent('Line: 1.');
    expect(screen.queryByRole('button', { name: 'Post payout po_1' })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Close review of payout po_1' }));
    expect(screen.queryByText('The return was refused.')).not.toBeInTheDocument();
  });

  it('describes a processor fee line, a payout still being checked, and a reason it does not know', async () => {
    server.use(
      http.get('/api/payments/settlements', () =>
        HttpResponse.json({
          items: [
            payout({
              status: 'Received',
              reason: null,
              postedAt: null,
              items: [line('1', 'Fee', 0, 4, -4, { paymentId: null, feeEntryId: 'f1' })],
            }),
            payout({
              id: 'settlement-2',
              payoutReference: 'po_2',
              status: 'NeedsReview',
              reason: 'something_new',
              postedAt: null,
              canClose: true,
            }),
          ],
        }),
      ),
    );
    show();
    const checking = await screen.findByRole('listitem', { name: 'Payout po_1' });
    expect(within(checking).getByText('Waiting to be checked')).toBeVisible();
    expect(
      within(within(checking).getAllByRole('row')[1]!)
        .getAllByRole('cell')
        .map((cell) => cell.textContent),
    ).toEqual(['1', 'Processor fee', '—', '$4.00', '−$4.00', 'Fee difference on the ledger']);
    const unknown = screen.getByRole('listitem', { name: 'Payout po_2' });
    expect(unknown).toHaveTextContent('Accounting refused this payout, so nothing was posted.');
    expect(unknown).toHaveTextContent('Reason: something_new');
    expect(within(unknown).queryByRole('button', { name: /Post payout/ })).not.toBeInTheDocument();
    expect(within(unknown).getByRole('button', { name: /Close review/ })).toBeEnabled();
  });

  it('closes a payout review with a required note and never without one', async () => {
    let current = waiting;
    let sent: unknown;
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.get('/api/payments/settlements', () => HttpResponse.json({ items: [current] })),
      http.post('/api/payments/settlements/settlement-1/close', async ({ request }) => {
        sent = await request.json();
        current = payout({
          ...waiting,
          status: 'Closed',
          canPost: false,
          canClose: false,
          reviewNote: 'Corrected by hand.',
        });
        return HttpResponse.json(current);
      }),
    );
    show();
    await userEvent.click(
      await screen.findByRole('button', { name: 'Close review of payout po_1' }),
    );
    const submit = screen.getByRole('button', { name: 'Close review' });
    expect(submit).toBeDisabled();
    const note = screen.getByLabelText('How was this resolved? (staff only)');
    expect(note).toHaveFocus();
    await userEvent.type(note, 'Corrected by hand.{Enter}');

    expect(await screen.findByText('Closed — nothing posted')).toBeVisible();
    expect(sent).toEqual({ note: 'Corrected by hand.' });
    expect(screen.getByText('Review closed by staff: Corrected by hand.')).toBeVisible();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    // A closed payout says why it waited, by code, but no longer tells anyone to post it.
    const closed = screen.getByRole('listitem', { name: 'Payout po_1' });
    expect(closed).toHaveTextContent('Reason: settlement_requires_confirmation');
    expect(closed).not.toHaveTextContent('it waits for an administrator');
    await waitFor(() => expect(within(closed).getByText('Payout po_1')).toHaveFocus());
  });

  it('shows staff who are not administrators the payout and its reason, without the actions', async () => {
    server.use(
      http.get('/api/payments/settlements', () => HttpResponse.json({ items: [waiting] })),
    );
    show(false);
    const item = await screen.findByRole('listitem', { name: 'Payout po_1' });
    expect(item).toHaveTextContent('An administrator can act on this payout.');
    expect(within(item).queryByRole('button')).not.toBeInTheDocument();
  });
});
