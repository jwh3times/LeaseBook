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

  // A sandbox payout as the processor first reports it: no bank has said anything (ADR-054).
  const held = payout({
    status: 'AwaitingBank',
    bankAmount: null,
    bankDate: null,
    postedAt: null,
    processorAmount: 992.5,
    failedAt: null,
    canPost: false,
    canClose: true,
    items: [line('1', 'Payment', 515.24, 22.74, 492.5), line('2', 'Payment', 500, 0, 500)],
  });
  const shows = async (...items: object[]) => {
    server.use(http.get('/api/payments/settlements', () => HttpResponse.json({ items })));
    show();
    return screen.findByRole('listitem', { name: 'Payout po_1' });
  };

  it('shows a held payout as waiting for the bank, with the processor’s amount and no bank amount or date', async () => {
    const item = await shows(held);
    expect(within(item).getByText('Waiting for the bank')).toBeVisible();
    expect(item).toHaveTextContent(
      'No bank evidence has arrived for this payout. Nothing posts until the bank says what it received.',
    );
    expect(item).toHaveTextContent('Processor’s payout amount: $992.50');
    expect(item).not.toHaveTextContent(/at the bank on/);
    expect(item).not.toHaveTextContent('$0.00');
    expect(item).not.toHaveTextContent(/null|NaN|Invalid/);
    // It cannot be posted, and can still be closed: the evidence may never come.
    expect(within(item).queryByRole('button', { name: /Post payout/ })).not.toBeInTheDocument();
    expect(within(item).getByRole('button', { name: 'Close review of payout po_1' })).toBeEnabled();
  });

  it('never shows a bank amount or date for a payout with none, whatever its status', async () => {
    const item = await shows(
      payout({
        ...held,
        status: 'NeedsReview',
        reason: 'payout_failed',
        failedAt: '2026-02-07T09:30:00Z',
      }),
    );
    expect(item).toHaveTextContent('No bank evidence has arrived for this payout.');
    expect(item).not.toHaveTextContent(/Nothing posts until/);
    expect(item).not.toHaveTextContent(/at the bank on/);
    expect(item).not.toHaveTextContent('$0.00');
  });

  it('says a payout that failed before posting was not posted, and when the processor reported it', async () => {
    const item = await shows(
      payout({
        ...held,
        status: 'NeedsReview',
        reason: 'payout_failed',
        failedAt: '2026-02-07T09:30:00Z',
      }),
    );
    expect(within(item).getByText('Needs review — nothing posted')).toBeVisible();
    expect(item).toHaveTextContent(
      'The processor reported that this payout failed. The payout was not posted, and nothing was reversed. Review it, then close the review.',
    );
    const failed = within(item).getByText(new Date('2026-02-07T09:30:00Z').toLocaleString());
    expect(failed).toHaveAttribute('datetime', '2026-02-07T09:30:00Z');
    expect(failed.parentElement).toHaveTextContent(
      /^The processor reported that this payout failed on .+\.$/,
    );
    expect(within(item).queryByRole('button', { name: /Post payout/ })).not.toBeInTheDocument();
  });

  it('says a payout that failed after posting posted and was not reversed, never that nothing posted', async () => {
    const item = await shows(
      payout({
        status: 'NeedsReview',
        reason: 'payout_failed',
        processorAmount: 992.5,
        failedAt: '2026-02-09T09:30:00Z',
        canClose: true,
      }),
    );
    expect(within(item).getByText('Needs review — posted, nothing reversed')).toBeVisible();
    expect(item).not.toHaveTextContent(/nothing posted/);
    expect(item).toHaveTextContent(
      'The processor reported that this payout failed after it was posted. Nothing was reversed automatically, so the ledger still shows it. Correct the ledger, then close the review.',
    );
    expect(item).toHaveTextContent('$992.50 at the bank on 2026-02-06');
  });

  it('keeps saying a closed payout posted when it did', async () => {
    const item = await shows(
      payout({ status: 'Closed', reason: 'payout_failed', failedAt: '2026-02-09T09:30:00Z' }),
    );
    expect(within(item).getByText('Closed — posted, nothing reversed')).toBeVisible();
    expect(item).not.toHaveTextContent(/nothing posted/);
    // Closed: the code stays, the instruction does not.
    expect(item).toHaveTextContent('Reason: payout_failed');
    expect(item).not.toHaveTextContent(/Correct the ledger/);
  });

  it('says the bank and the processor disagree on the amount, and shows both', async () => {
    const item = await shows(
      payout({
        status: 'NeedsReview',
        reason: 'bank_amount_mismatch',
        postedAt: null,
        bankAmount: 990,
        processorAmount: 992.5,
        canClose: true,
      }),
    );
    expect(within(item).getByText('Needs review — nothing posted')).toBeVisible();
    expect(item).toHaveTextContent(
      'The bank received a different amount from the one the processor says it paid, so nothing was posted. Review it, then close the review.',
    );
    expect(item).toHaveTextContent('$990.00 at the bank on 2026-02-06');
    expect(item).toHaveTextContent('Processor’s payout amount: $992.50');
  });

  it('shows no processor amount for a payout the processor did not report apart from the bank', async () => {
    const item = await shows(payout());
    expect(item).not.toHaveTextContent(/Processor’s payout amount/);
    expect(item).not.toHaveTextContent(/No bank evidence/);
  });

  it('names every line kind in words, and an unknown or empty one as unrecognised', async () => {
    const item = await shows(
      payout({
        items: ['Dispute', 'Refund', 'FailedDebit', 'Unknown', 'Reserve', ''].map((kind, index) =>
          line(String(index + 1), kind, 10, 0, -10),
        ),
      }),
    );
    expect(
      within(item)
        .getAllByRole('row')
        .slice(1)
        .map((row) => within(row).getAllByRole('cell')[1]!.textContent),
    ).toEqual([
      'Card dispute',
      'Refund',
      'Failed bank debit',
      'Unrecognised line',
      'Unrecognised line (Reserve)',
      'Unrecognised line',
    ]);
  });

  it('tells an administrator in words that a payout no bank has confirmed cannot be posted', async () => {
    // The action is not offered for a held payout; this is the list being a moment out of date.
    const stale = payout({ ...held, status: 'Received', canPost: true });
    server.use(
      http.get('/api/auth/csrf', () => new HttpResponse(null, { status: 204 })),
      http.post('/api/payments/settlements/settlement-1/post', () =>
        HttpResponse.json(
          {
            code: 'bank_evidence_missing',
            title: 'bank_evidence_missing',
            detail: 'The bank has not yet confirmed this payout, so nothing was posted.',
            correlationId: '1234567890abcdef1234567890abcdef',
          },
          { status: 409 },
        ),
      ),
    );
    const item = await shows(stale);
    await userEvent.click(within(item).getByRole('button', { name: 'Post payout po_1' }));
    const alert = await within(item).findByRole('alert');
    expect(alert).toHaveTextContent(
      'The bank has not yet confirmed this payout, so nothing was posted.',
    );
    expect(alert).toHaveTextContent('Reference: 1234567890abcdef1234567890abcdef');
    expect(alert).not.toHaveTextContent('bank_evidence_missing');
  });
});
